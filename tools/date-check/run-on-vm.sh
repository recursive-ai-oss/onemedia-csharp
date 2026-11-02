#!/usr/bin/env bash
# Copies the files of a folder to the Windows guest, reads the dates Explorer and Photos show, and copies the results back.
# Run it where the guest is already up (for example through the guest-session wrapper); it neither starts nor stops the guest.
#
#   tools/date-check/run-on-vm.sh <local-folder> <local-results-folder>
#
# Environment: GUEST_SSH_KEY, GUEST_SSH_PORT (default 2222), GUEST_USER (default oracle), PHOTOS_PATTERN (default "mp4 mov": files whose
# names contain one of these get a Photos screenshot).
set -u
src=$1; res=$2
key=${GUEST_SSH_KEY:?set GUEST_SSH_KEY}; port=${GUEST_SSH_PORT:-2222}; user=${GUEST_USER:-oracle}
opts=(-i "$key" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes -o ConnectTimeout=10)
G() { ssh "${opts[@]}" -p "$port" "$user@127.0.0.1" "$@"; }
mkdir -p "$res"
# The forwarded port accepts connections long before the guest answers; wait for a command to run.
for i in $(seq 1 120); do G hostname >/dev/null 2>&1 && break; sleep 5; done
G 'powershell -NoProfile -Command "Remove-Item -Recurse -Force C:\datecheck -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force C:\datecheck\files | Out-Null"'
scp -q -r "${opts[@]}" -P "$port" "$src"/. "$user@127.0.0.1:C:/datecheck/files/" || { echo copy failed; exit 2; }
scp -q "${opts[@]}" -P "$port" "$(dirname "$0")/read-dates.ps1" "$user@127.0.0.1:C:/datecheck/read-dates.ps1" || exit 2
run() { # <task-name> <extra args>
  G "schtasks /Create /TN dc /TR \"cmd /c cd /d C:\\datecheck && powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\read-dates.ps1 -Folder C:\\datecheck\\files -Out C:\\datecheck\\$1 $2 > C:\\datecheck\\$1.log 2>&1\" /SC ONCE /ST 00:00 /RU $user /IT /RL HIGHEST /F" > /dev/null
  G 'schtasks /Run /TN dc' > /dev/null
  for i in $(seq 1 200); do sleep 5; G "powershell -NoProfile -Command \"if (Select-String -Path C:\\datecheck\\$1.log -Pattern finished -Quiet) { 'done' }\"" 2>/dev/null | grep -q done && break; done
}
run props ''
for p in ${PHOTOS_PATTERN:-mp4 mov}; do run "photos-$p" "-Screenshots -Only $p"; done
scp -q -r "${opts[@]}" -P "$port" "$user@127.0.0.1:C:/datecheck/props" "$user@127.0.0.1:C:/datecheck/photos-*" "$res/" 2>&1 | tail -3
G 'type C:\datecheck\props.log' > "$res/props.log" 2>&1
ls -R "$res" | head -50
