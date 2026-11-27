# Copyright (c) 2026, zannagh. All rights reserved.
# See License in the project root for license information.
"""python3 -m unittest discover -s scripts/scan-upload: the upload refuses a base URL that would send the key in the clear."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import synthetic_scan  # noqa: E402


class CheckBaseTests(unittest.TestCase):
    def test_https_and_local_http_pass(self):
        for base in ("https://blocwerk.app", "https://host:8443/x", "http://localhost:5000", "http://127.0.0.1:5000"):
            synthetic_scan.check_base(base)

    def test_plain_http_elsewhere_and_other_schemes_are_refused(self):
        for base in ("http://blocwerk.app", "http://192.168.1.4:5000", "http://localhost.evil.example", "ftp://localhost", "https://"):
            with self.assertRaises(SystemExit, msg=base):
                synthetic_scan.check_base(base)


if __name__ == "__main__":
    unittest.main()
