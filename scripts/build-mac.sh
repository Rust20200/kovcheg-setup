#!/bin/bash
# Сборка EcoplanVPN для macOS: универсальное .app и EcoplanVPN-<версия>.dmg.
# На Linux кросс-компиляция dotnet (osx-arm64 и osx-x64). Образ .dmg на Mac
# собирает hdiutil, на Linux — mozilla/libdmg-hfsplus (нужны cmake, g++,
# zlib, bzip2, lzma, openssl и mkfs.hfsplus из hfsprogs).
# Боевой ключ подписи не нужен для компиляции. Чтобы клиент принял servers.json,
# передайте client.env (тот же, что для Windows): bash scripts/build-mac.sh client.env
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
if [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$DOTNET_ROOT:$PATH"
fi
command -v dotnet >/dev/null || { echo "нужен .NET 8 SDK: https://dot.net/v1/dotnet-install.sh"; exit 1; }
command -v python3 >/dev/null || { echo "нужен python3"; exit 1; }
command -v curl >/dev/null || { echo "нужен curl"; exit 1; }

ENV_FILE=${1:-$ROOT/client.env}
VERSION=$(python3 - << 'PY'
import re, pathlib
text = pathlib.Path("src/EcoplanVPN.Core/Product.cs").read_text()
m = re.search(r'Version = "([0-9.]+)"', text)
if not m:
    raise SystemExit("нет Product.Version")
print(m.group(1))
PY
)
DIST="$ROOT/dist"
WORK="$DIST/mac-work"
APP_NAME="EcoplanVPN"
DMG_NAME="${APP_NAME}-${VERSION}.dmg"
rm -rf "$WORK"
mkdir -p "$WORK/tools" "$DIST"

sha256_file() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | awk '{print $1}';
  else shasum -a 256 "$1" | awk '{print $1}'; fi
}

fetch_checked() {
  local url=$1 out=$2 expect=$3
  echo "качаю $url"
  curl -fL --retry 3 --retry-delay 2 -o "$out" "$url"
  local got
  got=$(sha256_file "$out")
  if [[ $got != "$expect" ]]; then
    echo "sha256 не сошёлся для $out: $got" >&2
    exit 1
  fi
}

# lock: имя архитектура url sha256
lookup() {
  local name=$1 arch=$2
  awk -v n="$name" -v a="$arch" '$1==n && $2==a {print $3, $4}' "$ROOT/scripts/mac-cores.lock"
}

install_core() {
  local name=$1 arch=$2 dest=$3
  local meta url sum archive
  meta=$(lookup "$name" "$arch")
  url=${meta%% *}
  sum=${meta##* }
  [[ -n $url && -n $sum ]] || { echo "нет записи $name $arch в mac-cores.lock"; exit 1; }
  archive="$WORK/cache/$(basename "$url")"
  mkdir -p "$WORK/cache" "$dest"
  if [[ ! -f $archive ]] || [[ $(sha256_file "$archive") != "$sum" ]]; then
    fetch_checked "$url" "$archive" "$sum"
  fi
  local unpack="$WORK/unpack-${name}-${arch}"
  rm -rf "$unpack"
  mkdir -p "$unpack"
  case "$archive" in
    *.zip) unzip -qo "$archive" -d "$unpack" ;;
    *.tar.gz|*.tgz) tar -xzf "$archive" -C "$unpack" ;;
    *) echo "неизвестный архив $archive"; exit 1 ;;
  esac
  local bin
  bin=$(find "$unpack" -type f -name "$name" | head -n 1)
  [[ -n $bin ]] || { echo "в архиве нет $name"; exit 1; }
  cp "$bin" "$dest/$name"
  chmod +x "$dest/$name"
}

HOST_ARCH=$(uname -m)
HOST_OS=$(uname -s)
case "$HOST_OS-$HOST_ARCH" in
  Linux-x86_64) HX=linux-amd64; HS=linux-amd64 ;;
  Darwin-arm64) HX=arm64; HS=arm64 ;;
  Darwin-x86_64) HX=x64; HS=x64 ;;
  *) HX=""; HS="" ;;
esac
if [[ -n $HX ]]; then
  install_core xray "$HX" "$WORK/tools"
  install_core sing-box "$HS" "$WORK/tools"
  export ECOPLAN_XRAY="$WORK/tools/xray"
  export ECOPLAN_SINGBOX="$WORK/tools/sing-box"
fi

python3 "$ROOT/scripts/restore-assets.py" || echo "иконки не восстановились — окно будет без логотипа"

echo "тесты"
dotnet test "$ROOT/src/EcoplanVPN.Core.Tests/EcoplanVPN.Core.Tests.csproj" -c Release --nologo

STAGE=$(mktemp -d)
cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT
cp -a "$ROOT/src" "$STAGE/src"
PUBSRC="$ROOT/src"
if [[ -f $ENV_FILE ]]; then
  python3 "$ROOT/scripts/stamp.py" "$ENV_FILE" --root "$STAGE/src"
  PUBSRC="$STAGE/src"
  echo "сборка со штампом $ENV_FILE"
else
  echo "нет $ENV_FILE — DMG без OTA_PUB. Подпись servers.json не сойдётся, пока не пересоберёте с client.env."
fi

publish_rid() {
  local rid=$1 out=$2
  echo "публикую $rid"
  dotnet publish "$PUBSRC/EcoplanVPN.Mac/EcoplanVPN.Mac.csproj" -c Release -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:UseAppHost=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$out"
}

publish_rid osx-arm64 "$WORK/osx-arm64"
publish_rid osx-x64 "$WORK/osx-x64"
install_core xray arm64 "$WORK/osx-arm64"
install_core sing-box arm64 "$WORK/osx-arm64"
install_core xray x64 "$WORK/osx-x64"
install_core sing-box x64 "$WORK/osx-x64"
cp "$ROOT/src/EcoplanVPN.Mac/Resources/ecoplan-priv.sh" "$WORK/osx-arm64/ecoplan-priv.sh"
cp "$ROOT/src/EcoplanVPN.Mac/Resources/ecoplan-priv.sh" "$WORK/osx-x64/ecoplan-priv.sh"
chmod +x "$WORK/osx-arm64/ecoplan-priv.sh" "$WORK/osx-x64/ecoplan-priv.sh"
LOGO="$ROOT/src/KovchegVPN/Resources/logo.png"
if [[ -f $LOGO ]]; then
  cp "$LOGO" "$WORK/osx-arm64/logo.png"
  cp "$LOGO" "$WORK/osx-x64/logo.png"
fi

APP="$WORK/dmg/${APP_NAME}.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
LIPO=""
for c in lipo llvm-lipo llvm-lipo-20 llvm-lipo-19 llvm-lipo-18 llvm-lipo-17; do
  if command -v "$c" >/dev/null; then LIPO=$c; break; fi
done

is_macho() { file "$1" | grep -q "Mach-O"; }

if [[ -n $LIPO ]]; then
  echo "универсальные Mach-O через $LIPO"
  cp -a "$WORK/osx-arm64/." "$APP/Contents/MacOS/"
  while IFS= read -r -d '' src; do
    rel=${src#"$WORK/osx-x64/"}
    dest="$APP/Contents/MacOS/$rel"
    if [[ -f $dest ]] && is_macho "$src" && is_macho "$dest"; then
      info_d=$("$LIPO" -info "$dest" 2>/dev/null || true)
      info_s=$("$LIPO" -info "$src" 2>/dev/null || true)
      # Skia/Avalonia уже поставляют fat dylib. Повторный lipo с тем же x86_64 падает.
      if [[ $info_d == *arm64* && $info_d == *x86_64* ]]; then
        :
      elif [[ $info_s == *arm64* && $info_s == *x86_64* ]]; then
        cp "$src" "$dest"
      elif "$LIPO" -create "$dest" "$src" -output "$dest.fat"; then
        mv "$dest.fat" "$dest"
        chmod +x "$dest" || true
      else
        rm -f "$dest.fat"
        echo "lipo: $rel оставляю без склейки" >&2
      fi
    elif [[ ! -e $dest ]]; then
      mkdir -p "$(dirname "$dest")"
      cp "$src" "$dest"
    fi
  done < <(find "$WORK/osx-x64" -type f -print0)
else
  echo "lipo нет — в .app два каталога и скрипт-запускатель"
  mkdir -p "$APP/Contents/MacOS/arm64" "$APP/Contents/MacOS/x64"
  cp -a "$WORK/osx-arm64/." "$APP/Contents/MacOS/arm64/"
  cp -a "$WORK/osx-x64/." "$APP/Contents/MacOS/x64/"
  cat > "$APP/Contents/MacOS/${APP_NAME}" << 'EOF'
#!/bin/bash
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
case "$(uname -m)" in
  arm64) exec "$HERE/arm64/EcoplanVPN" ;;
  x86_64) exec "$HERE/x64/EcoplanVPN" ;;
  *) echo "архитектура $(uname -m) не поддерживается" >&2; exit 1 ;;
esac
EOF
  chmod +x "$APP/Contents/MacOS/${APP_NAME}"
fi

cat > "$APP/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>${APP_NAME}</string>
  <key>CFBundleDisplayName</key><string>${APP_NAME}</string>
  <key>CFBundleIdentifier</key><string>com.ecoplan.vpn</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleExecutable</key><string>${APP_NAME}</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
EOF
printf 'APPL????' > "$APP/Contents/PkgInfo"

cat > "$WORK/dmg/Как установить.txt" << EOF
${APP_NAME} ${VERSION} для macOS (Apple Silicon и Intel).

1. Перетащите ${APP_NAME}.app в папку «Программы».
2. Если система пишет, что разработчик не подтверждён: правый клик по программе → «Открыть» → «Открыть».
   Или в Терминале:
   xattr -dr com.apple.quarantine /Applications/${APP_NAME}.app
3. «Весь Mac» спросит пароль администратора. Так запускается tun (sing-box) без Network Extension.
   «Только прокси» включает системный SOCKS. Подпись Apple и нотаризация в этой сборке не делались.
4. Российские сайты идут напрямую. YouTube и остальной зарубежный трафик — через выбранный сервер.
EOF
ln -sfn /Applications "$WORK/dmg/Applications"

# fanquake/libdmg-hfsplus больше не собирает hfsplus, только конвертер ISO.
# Берём mozilla/libdmg-hfsplus, у которого есть и запись HFS+, и dmg build.
LIBDMG_REV=e18579b391950589b9ed16ee630c54ce4319bb29

make_dmg_linux() {
  local src=$1 out=$2 vol=$3
  local toolroot="$WORK/libdmg"
  if [[ ! -x $toolroot/dmg/dmg || ! -x $toolroot/hfs/hfsplus ]]; then
    if ! command -v cmake >/dev/null || ! command -v g++ >/dev/null || ! command -v curl >/dev/null; then
      echo "для DMG на Linux нужны cmake, g++, curl, заголовки zlib/bzip2/lzma/openssl и mkfs.hfsplus (hfsprogs)." >&2
      echo "На Debian/Ubuntu: sudo apt-get install -y cmake g++ zlib1g-dev libbz2-dev liblzma-dev libssl-dev hfsprogs" >&2
      echo "каталог .app лежит в $src" >&2
      exit 1
    fi
    rm -rf "$toolroot"
    mkdir -p "$toolroot"
    curl -fL --retry 3 --retry-delay 2 -o "$toolroot/src.tar.gz" \
      "https://github.com/mozilla/libdmg-hfsplus/archive/${LIBDMG_REV}.tar.gz"
    tar -xzf "$toolroot/src.tar.gz" -C "$toolroot"
    mv "$toolroot/libdmg-hfsplus-${LIBDMG_REV}" "$toolroot/src"
    cmake -S "$toolroot/src" -B "$toolroot/src/build" -DCMAKE_BUILD_TYPE=Release
    cmake --build "$toolroot/src/build" -j"$(nproc 2>/dev/null || echo 2)"
    mkdir -p "$toolroot/dmg" "$toolroot/hfs"
    find "$toolroot/src/build" -type f -name dmg -perm -111 -exec cp {} "$toolroot/dmg/dmg" \;
    find "$toolroot/src/build" -type f -name hfsplus -perm -111 -exec cp {} "$toolroot/hfs/hfsplus" \;
  fi
  [[ -x $toolroot/dmg/dmg && -x $toolroot/hfs/hfsplus ]] || { echo "не собрался libdmg-hfsplus"; exit 1; }
  command -v mkfs.hfsplus >/dev/null || { echo "нужен mkfs.hfsplus из пакета hfsprogs" >&2; exit 1; }
  local bytes megs img hfs rel target mode
  bytes=$(du -sb "$src" | awk '{print $1}')
  megs=$(( bytes / 1024 / 1024 + 256 ))
  img="$WORK/raw.hfs"
  rm -f "$img"
  dd if=/dev/zero of="$img" bs=1M count="$megs" status=none
  # -s: case-sensitive. Утилита dmg пишет ASR только для такого тома.
  mkfs.hfsplus -s -v "$vol" "$img" >/dev/null
  hfs="$toolroot/hfs/hfsplus"
  while IFS= read -r path; do
    rel=${path#"$src"/}
    if [[ -L $path ]]; then
      target=$(readlink "$path")
      "$hfs" "$img" symlink "/$rel" "$target"
    elif [[ -d $path ]]; then
      "$hfs" "$img" mkdir "/$rel"
      mode=$(stat -c '%a' "$path")
      "$hfs" "$img" chmod "$mode" "/$rel"
    else
      "$hfs" "$img" add "$path" "/$rel"
      mode=$(stat -c '%a' "$path")
      "$hfs" "$img" chmod "$mode" "/$rel"
    fi
  done < <(find "$src" -mindepth 1 -print | sort)
  local check=$WORK/dmg-check
  mkdir -p "$check"
  for rel in \
    "EcoplanVPN.app/Contents/MacOS/EcoplanVPN" \
    "EcoplanVPN.app/Contents/MacOS/xray" \
    "EcoplanVPN.app/Contents/MacOS/sing-box" \
    "EcoplanVPN.app/Contents/MacOS/ecoplan-priv.sh"
  do
    "$hfs" "$img" extract "/$rel" "$check/$(basename "$rel")"
    cmp -s "$src/$rel" "$check/$(basename "$rel")" || { echo "в образе не сошёлся $rel" >&2; exit 1; }
  done
  "$toolroot/dmg/dmg" build "$img" "$out"
  python3 - "$out" << 'PY'
import sys
data = open(sys.argv[1], "rb").read()
if b"koly" not in data[-512:]:
    raise SystemExit("DMG без хвоста UDIF (koly) — это не образ macOS")
PY
}

OUT="$DIST/$DMG_NAME"
rm -f "$OUT"
if command -v hdiutil >/dev/null; then
  hdiutil create -volname "$APP_NAME" -srcfolder "$WORK/dmg" -ov -format UDZO "$OUT"
else
  make_dmg_linux "$WORK/dmg" "$OUT" "$APP_NAME"
fi
echo "готово: $OUT"
ls -lh "$OUT"
