#!/bin/bash
# Запускается через osascript «with administrator privileges».
# Делает только tun start/stop и системный SOCKS. Произвольный shell не принимает.
# bash 3.2 (macOS) — без массивов и прочих расширений.
set -eu

die() { echo "ecoplan-priv: $*" >&2; exit 1; }

abs_path() {
  case "$1" in
    /*) ;;
    *) die "нужен абсолютный путь" ;;
  esac
  case "$1" in
    *$'\n'*|*$'\r'*) die "путь с переводом строки" ;;
  esac
}

cmd="${1:-}"
shift || true

case "$cmd" in
  tun-start)
    singbox="${1:-}"; config="${2:-}"; pidfile="${3:-}"; logfile="${4:-}"; owner="${5:-}"
    abs_path "$singbox"; abs_path "$config"; abs_path "$pidfile"; abs_path "$logfile"
    [ "$(basename "$singbox")" = "sing-box" ] || die "ожидался sing-box"
    [ -x "$singbox" ] || die "sing-box не исполняемый"
    [ -f "$config" ] || die "нет конфига"
    case "$config" in
      *.json) ;;
      *) die "конфиг не json" ;;
    esac
    mkdir -p "$(dirname "$pidfile")" "$(dirname "$logfile")"
    if [ -f "$pidfile" ]; then
      old="$(cat "$pidfile" 2>/dev/null || true)"
      case "$old" in
        ''|*[!0-9]*) ;;
        *)
          kill "$old" 2>/dev/null || true
          sleep 0.3
          ;;
      esac
    fi
    nohup "$singbox" run -c "$config" >>"$logfile" 2>&1 &
    echo $! > "$pidfile"
    if [ -n "$owner" ]; then
      chown "$owner" "$pidfile" "$logfile" 2>/dev/null || true
    fi
    chmod 644 "$pidfile" "$logfile" 2>/dev/null || true
    echo "ok pid=$(cat "$pidfile")"
    ;;
  tun-stop)
    pidfile="${1:-}"
    abs_path "$pidfile"
    if [ -f "$pidfile" ]; then
      old="$(cat "$pidfile" 2>/dev/null || true)"
      case "$old" in
        ''|*[!0-9]*) ;;
        *)
          kill "$old" 2>/dev/null || true
          sleep 0.4
          kill -9 "$old" 2>/dev/null || true
          ;;
      esac
      rm -f "$pidfile"
    fi
    echo ok
    ;;
  proxy-on)
    host="${1:-}"; port="${2:-}"; state="${3:-}"
    abs_path "$state"
    case "$host" in
      127.0.0.1|localhost) ;;
      *) die "прокси только на localhost" ;;
    esac
    case "$port" in
      ''|*[!0-9]*) die "порт" ;;
    esac
    mkdir -p "$(dirname "$state")"
    : > "$state"
    /usr/sbin/networksetup -listallnetworkservices | tail -n +2 | while IFS= read -r svc; do
      case "$svc" in
        \**) continue ;;
      esac
      [ -n "$svc" ] || continue
      info="$(/usr/sbin/networksetup -getsocksfirewallproxy "$svc" 2>/dev/null || true)"
      en="$(printf '%s\n' "$info" | awk -F': ' '/^Enabled:/{print $2; exit}')"
      sv="$(printf '%s\n' "$info" | awk -F': ' '/^Server:/{print $2; exit}')"
      pt="$(printf '%s\n' "$info" | awk -F': ' '/^Port:/{print $2; exit}')"
      printf '%s\t%s\t%s\t%s\n' "$svc" "${en:-No}" "${sv:-}" "${pt:-0}" >> "$state"
      /usr/sbin/networksetup -setsocksfirewallproxy "$svc" "$host" "$port" off || true
      /usr/sbin/networksetup -setsocksfirewallproxystate "$svc" on || true
      /usr/sbin/networksetup -setproxybypassdomains "$svc" \
        localhost 127.0.0.1 "*.local" "169.254.0.0/16" || true
    done
    echo ok
    ;;
  proxy-off)
    state="${1:-}"
    abs_path "$state"
    if [ -f "$state" ]; then
      while IFS="$(printf '\t')" read -r svc en sv pt; do
        [ -n "$svc" ] || continue
        case "$en" in
          Yes|yes)
            /usr/sbin/networksetup -setsocksfirewallproxy "$svc" "$sv" "$pt" off || true
            /usr/sbin/networksetup -setsocksfirewallproxystate "$svc" on || true
            ;;
          *)
            /usr/sbin/networksetup -setsocksfirewallproxystate "$svc" off || true
            ;;
        esac
      done < "$state"
      rm -f "$state"
    else
      /usr/sbin/networksetup -listallnetworkservices | tail -n +2 | while IFS= read -r svc; do
        case "$svc" in
          \**) continue ;;
        esac
        /usr/sbin/networksetup -setsocksfirewallproxystate "$svc" off || true
      done
    fi
    echo ok
    ;;
  *)
    die "неизвестная команда"
    ;;
esac
