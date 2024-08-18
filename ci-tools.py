#!/usr/bin/env python3

import xml.etree.ElementTree as ET
from os import environ
from sys import argv, exit

def get_version():
    with open('heliosphere-plugin.csproj') as f:
        tree = ET.parse(f)

    root = tree.getroot()
    items = list(filter(
        lambda version: version is not None,
        map(
            lambda child: child.find('Version'),
            root,
        ),
    ))

    if len(items) == 0:
        return None

    return items[0].text

def is_testing():
    return environ.get('GITHUB_REF_NAME') == 'testing'

def main():
    subcommand = argv[1]
    if subcommand == 'get-version':
        version = get_version()
        if version is None:
            exit(1)
        print(version)
    elif subcommand == 'is-testing':
        exit(0 if is_testing() else 1)
    else:
        print(f'unknown subcommand {subcommand}')
        exit(1)

if __name__ == '__main__':
    main()
