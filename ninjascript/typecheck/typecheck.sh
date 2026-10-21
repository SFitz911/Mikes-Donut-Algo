#!/bin/sh
# Linux-only C# TYPE CHECK of the single-machine NinjaScript files against hand-written NT8 stand-ins (nt8stubs.cs).
# This is NOT a NinjaTrader compile: stub signatures are from memory / from the deployed FitzLabsBridge.cs usage.
# Needs mono `mcs`. Run from single_machine/ninjascript/typecheck.
set -e
mcs -target:library -nowarn:67,169,649 -out:/tmp/fitzsa_typecheck.dll nt8stubs.cs \
  ../AddOns/FitzLabsStandaloneBridge.cs ../Indicators/FitzLabsBarWriter.cs
echo "typecheck OK (stubs only; real NT8 compile = F5 on the target PC)"
