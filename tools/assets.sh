#!/bin/bash
# install: back up originals (reflink) and copy patched assets in. uninstall: restore backups.
set -euo pipefail
G="/data/dev/workspace/SteamLibrary/steamapps/common/Heroes of Might and Magic Olden Era/HeroesOldenEra_Data"
R="$(cd "$(dirname "$0")/.." && pwd)"; S="$R/staging"; B="$R/backup-orig"
pgrep -f '^S:.*HeroesOldenEra.exe' >/dev/null && { echo "close the game first"; exit 1; }
case "${1:-}" in
install)
  mkdir -p "$B"
  awk '/bc7=/{match($0,/stream_flagged=([0-9]+)/,s); match($0,/bc7=([0-9]+)/,c); if (s[1]>0||c[1]>0) {sub(":","",$1); print $1}}' "$S/patch.log" | while read -r f; do
    [ -e "$B/$f" ] || cp --reflink=auto "$G/$f" "$B/$f"
    cp "$S/$f" "$G/$f"; echo "patched $f"
  done ;;
uninstall)
  for f in "$B"/*; do cp --reflink=auto "$f" "$G/$(basename "$f")"; echo "restored $(basename "$f")"; done ;;
*) echo "usage: $0 install|uninstall"; exit 2 ;;
esac
