#!/usr/bin/env bash
# Git pre-commit guard: no client binaries, decompiled code, large files or secrets in git.
# Install: ln -sf ../../scripts/pre-commit.sh .git/hooks/pre-commit
set -euo pipefail

fail=0
while IFS= read -r -d '' f; do
  case "$f" in
    *.dll|*.dylib|*.bundle/*|*.assets|*.resS|*.app/*|*.exe|*.so|*.db|*.sqlite*|*.pcap*|google-services*.json|*/google-services*.json)
      echo "pre-commit: blocked file type: $f" >&2; fail=1; continue ;;
    decompiled/*|*/decompiled/*|snapshots/*|*/snapshots/*|client/*|*/client/*)
      echo "pre-commit: blocked path: $f" >&2; fail=1; continue ;;
  esac
  size=$(git cat-file -s ":$f")
  # inventories/ holds our own text manifests (the full hash list is ~6 MB)
  if [[ "$f" != inventories/*.txt ]] && (( size > 5000000 )); then echo "pre-commit: file over 5 MB: $f" >&2; fail=1; fi
done < <(git diff --cached --name-only --diff-filter=ACMR -z)

# Token-like strings in added lines: Google API keys, private keys, JWTs, Steam IDs
if git diff --cached -U0 --diff-filter=ACMR | grep -E '^\+' | grep -v '^+++' \
   | grep -nE 'AIza[0-9A-Za-z_-]{35}|-----BEGIN [A-Z ]*PRIVATE KEY|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.|7656119[0-9]{10}' >&2; then
  echo "pre-commit: token-like string in staged changes (redact it)" >&2; fail=1
fi

exit $fail
