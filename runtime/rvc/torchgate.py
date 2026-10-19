# -*- coding: utf-8 -*-
"""TorchGate——谱门降噪（RVC 实时链的输入 / 输出降噪）。

来源：RVC 整合包 `RVC20240604Nvidia/tools/torchgate/{torchgate.py, utils.py}`（原样语义）。
自持化的两处改动：
  ① 去掉 `from infer.lib.rmvpe import STFT` 与 `privateuseone`（DirectML）分支——本机走 CUDA / CPU，
     只用 `torch.stft` / `torch.istft` 一条路；
  ② 内联 utils 的 `amp_to_db` / `temperature_sigmoid` / `linspace`（零新依赖，不引整合包）。
其余算法（平稳掩码 / 非平稳掩码 / 掩码平滑 / 谱门衰减）与原版逐行一致。
"""
import torch
from torch.nn.functional import conv1d, conv2d


def amp_to_db(x: torch.Tensor, eps=torch.finfo(torch.float64).eps, top_db=40) -> torch.Tensor:
    """幅度谱转 dB（原版 utils.amp_to_db）。"""
    x_db = 20 * torch.log10(x.abs() + eps)
    return torch.max(x_db, (x_db.max(-1).values - top_db).unsqueeze(-1))


def temperature_sigmoid(x: torch.Tensor, x0: float, temp_coeff: float) -> torch.Tensor:
    """带温度系数的 sigmoid（原版 utils.temperature_sigmoid）。"""
    return torch.sigmoid((x - x0) / temp_coeff)


def _linspace(start, stop, num: int = 50, endpoint: bool = True, **kwargs) -> torch.Tensor:
    """线性等分（原版 utils.linspace——endpoint=False 时少取一个点）。"""
    if endpoint:
        return torch.linspace(start, stop, num, **kwargs)
    return torch.linspace(start, stop, num + 1, **kwargs)[:-1]


class TorchGate(torch.nn.Module):
    """谱门降噪模块——`forward(x, xn)` 返回与 x 同形的降噪波形。

    sr：采样率；n_fft：STFT 窗长；prop_decrease：掩码处衰减比例（1.0 = 全减）。
    """

    @torch.no_grad()
    def __init__(
        self,
        sr: int,
        nonstationary: bool = False,
        n_std_thresh_stationary: float = 1.5,
        n_thresh_nonstationary: float = 1.3,
        temp_coeff_nonstationary: float = 0.1,
        n_movemean_nonstationary: int = 20,
        prop_decrease: float = 1.0,
        n_fft: int = 1024,
        win_length: int = None,
        hop_length: int = None,
        freq_mask_smooth_hz: float = 500,
        time_mask_smooth_ms: float = 50,
    ):
        super().__init__()

        self.sr = sr
        self.nonstationary = nonstationary
        assert 0.0 <= prop_decrease <= 1.0
        self.prop_decrease = prop_decrease

        self.n_fft = n_fft
        self.win_length = self.n_fft if win_length is None else win_length
        self.hop_length = self.win_length // 4 if hop_length is None else hop_length

        self.n_std_thresh_stationary = n_std_thresh_stationary
        self.temp_coeff_nonstationary = temp_coeff_nonstationary
        self.n_movemean_nonstationary = n_movemean_nonstationary
        self.n_thresh_nonstationary = n_thresh_nonstationary

        self.freq_mask_smooth_hz = freq_mask_smooth_hz
        self.time_mask_smooth_ms = time_mask_smooth_ms
        self.register_buffer("smoothing_filter", self._generate_mask_smoothing_filter())

    @torch.no_grad()
    def _generate_mask_smoothing_filter(self):
        """生成掩码平滑核（频域 × 时域外积，归一化）；两维都关时返回 None。"""
        if self.freq_mask_smooth_hz is None and self.time_mask_smooth_ms is None:
            return None

        n_grad_freq = (
            1
            if self.freq_mask_smooth_hz is None
            else int(self.freq_mask_smooth_hz / (self.sr / (self.n_fft / 2)))
        )
        if n_grad_freq < 1:
            raise ValueError(
                f"freq_mask_smooth_hz needs to be at least {int((self.sr / (self.n_fft / 2)))} Hz"
            )

        n_grad_time = (
            1
            if self.time_mask_smooth_ms is None
            else int(self.time_mask_smooth_ms / ((self.hop_length / self.sr) * 1000))
        )
        if n_grad_time < 1:
            raise ValueError(
                f"time_mask_smooth_ms needs to be at least {int((self.hop_length / self.sr) * 1000)} ms"
            )

        if n_grad_time == 1 and n_grad_freq == 1:
            return None

        v_f = torch.cat(
            [
                _linspace(0, 1, n_grad_freq + 1, endpoint=False),
                _linspace(1, 0, n_grad_freq + 2),
            ]
        )[1:-1]
        v_t = torch.cat(
            [
                _linspace(0, 1, n_grad_time + 1, endpoint=False),
                _linspace(1, 0, n_grad_time + 2),
            ]
        )[1:-1]
        smoothing_filter = torch.outer(v_f, v_t).unsqueeze(0).unsqueeze(0)

        return smoothing_filter / smoothing_filter.sum()

    @torch.no_grad()
    def _stationary_mask(self, X_db: torch.Tensor, xn: torch.Tensor = None) -> torch.Tensor:
        """平稳噪声掩码——按对数幅度谱的均值 + N 倍标准差切一刀。"""
        if xn is not None:
            XN = torch.stft(
                xn,
                n_fft=self.n_fft,
                hop_length=self.hop_length,
                win_length=self.win_length,
                return_complex=True,
                pad_mode="constant",
                center=True,
                window=torch.hann_window(self.win_length).to(xn.device),
            )
            XN_db = amp_to_db(XN).to(dtype=X_db.dtype)
        else:
            XN_db = X_db

        std_freq_noise, mean_freq_noise = torch.std_mean(XN_db, dim=-1)
        noise_thresh = mean_freq_noise + std_freq_noise * self.n_std_thresh_stationary
        sig_mask = X_db > noise_thresh.unsqueeze(2)
        return sig_mask

    @torch.no_grad()
    def _nonstationary_mask(self, X_abs: torch.Tensor) -> torch.Tensor:
        """非平稳噪声掩码——按「比滑动均值慢多少」出掩码。"""
        X_smoothed = (
            conv1d(
                X_abs.reshape(-1, 1, X_abs.shape[-1]),
                torch.ones(
                    self.n_movemean_nonstationary,
                    dtype=X_abs.dtype,
                    device=X_abs.device,
                ).view(1, 1, -1),
                padding="same",
            ).view(X_abs.shape)
            / self.n_movemean_nonstationary
        )

        slowness_ratio = (X_abs - X_smoothed) / (X_smoothed + 1e-6)
        sig_mask = temperature_sigmoid(
            slowness_ratio, self.n_thresh_nonstationary, self.temp_coeff_nonstationary
        )
        return sig_mask

    def forward(self, x: torch.Tensor, xn: torch.Tensor = None) -> torch.Tensor:
        """对 x 做谱门降噪；xn 给噪声参考（None = 用 x 自己估）。"""
        X = torch.stft(
            x,
            n_fft=self.n_fft,
            hop_length=self.hop_length,
            win_length=self.win_length,
            return_complex=True,
            pad_mode="constant",
            center=True,
            window=torch.hann_window(self.win_length).to(x.device),
        )

        if self.nonstationary:
            sig_mask = self._nonstationary_mask(X.abs())
        else:
            sig_mask = self._stationary_mask(amp_to_db(X), xn)

        sig_mask = self.prop_decrease * (sig_mask.float() - 1.0) + 1.0

        if self.smoothing_filter is not None:
            sig_mask = conv2d(
                sig_mask.unsqueeze(1),
                self.smoothing_filter.to(sig_mask.dtype),
                padding="same",
            )

        Y = X * sig_mask.squeeze(1)
        y = torch.istft(
            Y,
            n_fft=self.n_fft,
            hop_length=self.hop_length,
            win_length=self.win_length,
            center=True,
            window=torch.hann_window(self.win_length).to(Y.device),
        )
        return y.to(dtype=x.dtype)
