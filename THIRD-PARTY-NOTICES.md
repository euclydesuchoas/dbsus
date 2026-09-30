# Third-party notices

DbSus is licensed under the MIT license (see `LICENSE`). It includes code derived from the third-party software listed below, under its own license.

## blast.c (PKWare DCL "implode" decompressor)

- **Author:** Mark Adler
- **Repository:** https://github.com/madler/zlib
- **Branch:** `develop`
- **Commit:** `767c4c947852e143f582c85f14cf573411df1b35`
- **Files:** `contrib/blast/blast.c` and `contrib/blast/blast.h`
- **Version (from the file header):** 1.3, 24 Aug 2013
- **License:** zlib
- **Used in:** `src/DbSus/Dbc/Implode/BlastDecoder.cs`, a C# port that is **altered from the original**. It is rewritten as a pull-based decoder that can suspend and resume mid-copy, reads from a `Stream`, reports errors as exceptions and decodes Huffman codes one bit at a time. The upstream test vector (`contrib/blast/test/test.pk` → `test.txt`) is used in the unit tests.

```
Copyright (C) 2003, 2012, 2013 Mark Adler
version 1.3, 24 Aug 2013

This software is provided 'as-is', without any express or implied
warranty.  In no event will the author be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.

Mark Adler    madler@alumni.caltech.edu
```
