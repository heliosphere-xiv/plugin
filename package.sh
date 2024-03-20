#!/usr/bin/zsh

python3 make_repo.py \
  bin/x64/Release/net8.0-windows/heliosphere-plugin/latest.zip \
  ./changelog.txt \
  https://repo.heliosphere.app/latest.zip \
  repo.json
