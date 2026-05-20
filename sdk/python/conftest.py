"""Shared pytest configuration for the sdk/python test suite."""
import sys
import os

# Make vais_extension importable: the package lives in sdk/python/vais_extension/
_ext_path = os.path.join(os.path.dirname(__file__), "vais_extension")
if _ext_path not in sys.path:
    sys.path.insert(0, _ext_path)
