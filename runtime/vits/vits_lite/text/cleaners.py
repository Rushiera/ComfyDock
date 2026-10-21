"""MoeGoe 文本清理链的日语子集。

只保留本项目实际用到的两个 cleaner：
- ``japanese_cleaners``：日语汉字/假名 → 带重音标记的罗马字音素串（MoeGoe 原实现）
- ``identity_cleaner``：直通——对应 MoeGoe 的 ``[CLEANED]`` 标记（文本已是音素串，不再清理）

原始 cleaners.py 里其余语言（韩/中/粤/泰…）随依赖一起裁掉：346 模型只声明 japanese_cleaners。
"""

import re


def japanese_cleaners(text):
    from text.japanese import japanese_to_romaji_with_accent
    text = japanese_to_romaji_with_accent(text)
    text = re.sub(r'([A-Za-z])$', r'\1.', text)
    return text


def japanese_cleaners2(text):
    return japanese_cleaners(text).replace('ts', 'ʦ').replace('...', '…')


def identity_cleaner(text):
    return text
