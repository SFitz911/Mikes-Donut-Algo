"""Sim-only account rule (mirrored in the AddOn, which additionally requires Provider == Simulator)."""
import re

_SIM = re.compile(r"\ASim[A-Za-z0-9_-]{1,40}\Z")
_ANY = re.compile(r"\A[A-Za-z0-9_-]{1,44}\Z")


def account_allowed(name, allow_non_sim_names=False):
    if not isinstance(name, str): return False
    return bool((_ANY if allow_non_sim_names else _SIM).match(name))
