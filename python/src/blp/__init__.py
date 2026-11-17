"""The blp API over blp-net, the .NET Bloomberg library, through pythonnet.

Importing blp starts the .NET runtime and loads Blp.dll from the directory
that BLP_NET_DIR names. clr_loader finds the .NET install from DOTNET_ROOT,
else from the dotnet command on PATH.
"""
import os
import sys

from pythonnet import load

__version__ = '0.1.0'

load('coreclr')
import clr  # noqa: E402 - pythonnet creates clr only inside load()

sys.path.append(os.environ['BLP_NET_DIR'])
clr.AddReference('Blp')
from blp.client import *  # noqa: E402,F403 - imports .NET types from Blp.dll
