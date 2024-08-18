from os import path
from datetime import datetime
from requests import get
import argparse
import zipfile
import json


COPY_KEYS = [
    'Author',
    'Name',
    'Punchline',
    'Description',
    'InternalName',
    'RepoUrl',
    'ApplicableVersion',
    'IsHide',
    'IsTestingExclusive',
    'IconUrl',
    'Tags',
]

MAYBE_TESTING_COPY_KEYS = [
    'AssemblyVersion',
    'DalamudApiLevel',
]

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--archive', help='the zip archive of the plugin', required=True)
    parser.add_argument('--changelog', help='the changelog text file', required=True)
    parser.add_argument('--changelog-test', help='the changelog text file for testing mode')
    parser.add_argument('--download-main', help='the main download link', required=True)
    parser.add_argument('--download-test', help='the testing download link (if any)')
    parser.add_argument('--testing', help='enable/disable testing mode', action=argparse.BooleanOptionalAction, default=False)
    parser.add_argument('output', help='the output path of the generated repo')

    args = parser.parse_args()

    args = {
        'zip_path': args.archive,
        'changelog_path': args.changelog,
        'changelog_path_test': args.changelog_test,
        'download_link_main': args.download_main,
        'download_link_test': args.download_test,
        'output_path': args.output,
        'testing': args.testing
    }

    repo = get('https://repo.heliosphere.app/').json()[0]

    zip_file = zipfile.PyZipFile(args['zip_path'])
    json_file, json_data, changelog, changelog_test = None, None, None, None
    for f in zip_file.filelist:
        if f.filename.endswith('.json'):
            json_file = f
            with zip_file.open(json_file, 'r') as inner:
                json_text = inner.read()
            json_data = json.loads(json_text)
            if 'DalamudApiLevel' in json_data:
                break
            json_file = None
    if json_file is None or json_data is None:
        return
    if path.exists(args['changelog_path']):
        with open(args['changelog_path']) as f:
            changelog = f.read().strip()
    if args['changelog_path_test'] is not None and path.exists(args['changelog_path_test']):
        with open(args['changelog_path_test']) as f:
            changelog_test = f.read().strip()

    for key in COPY_KEYS:
        if key in json_data:
            repo[key] = json_data[key]

    for key in MAYBE_TESTING_COPY_KEYS:
        if key in json_data:
            prefix = 'Testing' if args['testing'] else ''
            repo[prefix + key] = json_data[key]

    repo['DownloadCount'] = 9001
    repo['DownloadLinkInstall'] = args['download_link_main']
    repo['DownloadLinkTesting'] = args['download_link_test'] if args['download_link_test'] is not None else args['download_link_main']
    repo['DownloadLinkUpdate'] = args['download_link_main']
    repo['IsHide'] = False
    repo['IsTestingExclusive'] = False

    if not args['testing']:
        repo['LastUpdate'] = int(datetime(*json_file.date_time).timestamp())

    if changelog is not None and len(changelog) > 0:
        repo['Changelog'] = changelog

    if changelog_test is not None and len(changelog_test) > 0:
        repo['TestingChangelog'] = changelog_test

    with open(args['output_path'], 'w') as f:
        json.dump([repo], f, indent=4, sort_keys=True)

    print(f'wrote to {args["output_path"]}')


if __name__ == '__main__':
    main()
