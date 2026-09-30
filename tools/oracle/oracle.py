"""Convert every .dbc under the given directories with pyreaddbc and print the expected hashes.

Output is JSON on stdout: for each input file, the SHA-256 and size of the .dbc itself (which
pins the exact file version, since DATASUS republishes files) and of the .dbf the oracle produces.
Usage: python oracle.py DIR [DIR ...]
"""

import hashlib
import json
import os
import sys
import tempfile

import pyreaddbc


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main(directories):
    entries = []
    for directory in directories:
        for name in sorted(os.listdir(directory)):
            if not name.lower().endswith(".dbc"):
                continue
            dbc_path = os.path.join(directory, name)
            with tempfile.TemporaryDirectory() as work:
                dbf_path = os.path.join(work, "out.dbf")
                pyreaddbc.dbc2dbf(dbc_path, dbf_path)
                entries.append({
                    "file": name,
                    "dbcSha256": sha256_of(dbc_path),
                    "dbcSize": os.path.getsize(dbc_path),
                    "dbfSha256": sha256_of(dbf_path),
                    "dbfSize": os.path.getsize(dbf_path),
                })
            print(f"converted {name}", file=sys.stderr)
    json.dump({"oracle": "pyreaddbc 2.0.4", "files": entries}, sys.stdout, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main(sys.argv[1:])
