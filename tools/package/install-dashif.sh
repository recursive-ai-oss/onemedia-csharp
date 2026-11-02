#!/usr/bin/env bash
# Installs the DASH-IF conformance validator (the Laravel release, run only as a command-line oracle, never shipped) user-locally under
# $ONEMEDIA_HOME/oracles/dashif: a static PHP 8.3 build, Temurin 21, composer and the release's PHP dependencies. No sudo, no containers.
# Downloads are pinned by SHA-256 (fill a pin in after the first run prints it; an empty pin only prints).
#   usage: install-dashif.sh        then:  run-dashif.sh <manifest-url>
set -euo pipefail
HOME_DIR="${ONEMEDIA_HOME:-$HOME/onemedia}"; D="$HOME_DIR/oracles/dashif"; mkdir -p "$D/dl"
TAG=v3.1.0
PHP_URL=https://dl.static-php.dev/static-php-cli/bulk/php-8.3.32-cli-linux-x86_64.tar.gz;               PHP_SHA=
JDK_URL=https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.13%2B9/OpenJDK21U-jdk_x64_linux_hotspot_21.0.13_9.tar.gz; JDK_SHA=d7f5d5cf865a2902b70ff549b97586a7d3f234e016f041ab447ea740c7d66d1c
COMPOSER_URL=https://getcomposer.org/download/2.8.12/composer.phar;                                       COMPOSER_SHA=
APP_URL=https://github.com/Dash-Industry-Forum/DASH-IF-Conformance/archive/refs/tags/$TAG.tar.gz;          APP_SHA=
get() { # url sha file
  local f="$D/dl/$3"; [ -s "$f" ] || curl -fsSL "$1" -o "$f"
  local got; got="$(sha256sum "$f" | cut -d' ' -f1)"; echo "$3 sha256 $got"
  [ -z "$2" ] || [ "$2" = "$got" ] || { echo "sha256 mismatch for $3" >&2; exit 1; }
}
get "$PHP_URL" "$PHP_SHA" php.tar.gz; get "$JDK_URL" "$JDK_SHA" jdk.tar.gz; get "$COMPOSER_URL" "$COMPOSER_SHA" composer.phar; get "$APP_URL" "$APP_SHA" app.tar.gz
mkdir -p "$D/php" "$D/jdk" "$D/app"
[ -x "$D/php/php" ] || tar -xzf "$D/dl/php.tar.gz" -C "$D/php"
[ -d "$D/jdk/bin" ] || tar -xzf "$D/dl/jdk.tar.gz" -C "$D/jdk" --strip-components=1
[ -f "$D/app/artisan" ] || tar -xzf "$D/dl/app.tar.gz" -C "$D/app" --strip-components=1
export PATH="$D/php:$D/jdk/bin:$HOME_DIR/oracles/bin:$PATH" JAVA_HOME="$D/jdk" COMPOSER_HOME="$D/composer-home"
cd "$D/app"
"$D/php/php" -r 'echo PHP_VERSION, " ", implode(",", array_intersect(["curl","dom","mbstring","pdo_sqlite","sqlite3","xml","openssl","zip","tokenizer","fileinfo","phar"], get_loaded_extensions())), "\n";'
[ -d vendor ] || "$D/php/php" "$D/dl/composer.phar" install --no-dev --no-interaction --no-progress --ignore-platform-reqs
[ -f .env ] || { cp env_production.example .env; "$D/php/php" artisan key:generate --force; }
mkdir -p database storage/logs; [ -f database/database.sqlite ] || touch database/database.sqlite
"$D/php/php" artisan migrate --force
echo installed
