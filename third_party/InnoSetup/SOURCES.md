# Inno Setup installer support

Roger's installer is compiled with Inno Setup 6.7.4. The compiler itself is a build tool and is not included in Roger's installed application. The generated installer contains Inno Setup runtime code and the Simplified Chinese language messages.

- Official release: https://github.com/jrsoftware/issrc/releases/tag/is-6_7_4
- Official compiler installer: https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe
- SHA-256: `a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c`
- Windows Authenticode publisher: `Pyrsys B.V.`.
- License: `LICENSE.txt`, copied unmodified from that official compiler distribution.
- Simplified Chinese messages: https://github.com/jrsoftware/issrc/blob/is-6_7_4/Files/Languages/Unofficial/ChineseSimplified.isl
- Original translation is retained unmodified in `installer/Languages/ChineseSimplified.isl`, including its translator attribution.
- Download verification: https://jrsoftware.org/isdl-verify.php
- Official portable extraction behavior: https://jrsoftware.org/ishelp/topic_technotes.htm and https://github.com/jrsoftware/issrc/blob/is-6_7_4/isportable.iss

The bootstrap build script uses the original installer in official `/PORTABLE=1 /CURRENTUSER /NOICONS` mode, writing only a workspace compiler directory and no Windows installation entry or file associations.
