#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------------
# Doc-15 offline shim gate: type-check the plugin GLUE (PluginCore / DecalGameState /
# DecalSpellTable) + NB3.Core against faithful API shims, with the real C# compiler at the
# project's LangVersion — on a machine with neither Decal nor VVS installed.
#
# What it catches: missing members, wrong overloads, namespace ambiguities (CS0104), the
# method-vs-property class of bug (CS8773), the MetaViewWrappers Path shadow (CS0120).
# What it can't:  anything the shims model incorrectly, and all runtime/VVS layout behaviour.
# The vendored VirindiViews wrapper files are NOT compiled here (they need the real VVS
# assembly); the shims stand in for their surface, per doc 15 §2.
#
# Usage:  bash tools/shimcheck/run-shimcheck.sh     (from the repo root)
# ---------------------------------------------------------------------------------------------
set -e
cd "$(dirname "$0")/../.."

CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | head -1)
if [ -z "$CSC" ]; then CSC=$(find "$(dirname "$(command -v dotnet)")" -path '*Roslyn/bincore/csc.dll' 2>/dev/null | head -1); fi
if [ -z "$CSC" ]; then echo "ERROR: csc.dll not found (need a .NET SDK)"; exit 2; fi

REFDIR=$(ls -d /usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net*.0 2>/dev/null | head -1)
if [ -z "$REFDIR" ]; then echo "ERROR: NETCore.App ref pack not found"; exit 2; fi

RSP=$(mktemp)
for dll in "$REFDIR"/*.dll; do echo "/reference:$dll" >> "$RSP"; done

OUT=$(mktemp -d)
# LangVersion 7.3 == the plugin csproj's LangVersion — the match is load-bearing (doc 15 §3).
dotnet "$CSC" /nologo /target:library /langversion:7.3 /nullable:disable /nowarn:CS0067,CS0169,CS0649 \
  /out:"$OUT/nb3_shimcheck.dll" @"$RSP" \
  $(find src/NB3.Core -name '*.cs') \
  $(find src/NB3.Plugin -maxdepth 1 -name '*.cs' | sort) \
  tools/shimcheck/Shims.cs

rm -f "$RSP"
echo "shimcheck: PASS — plugin glue type-checks against the doc-15 shims (langversion 7.3)"
