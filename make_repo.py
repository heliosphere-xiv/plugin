from sys import argv
from os import path
from datetime import datetime
import zipfile
import json


def main():
    if len(argv) < 4:
        print('make_repo.py [latest.zip] [changelog.txt] [download link] [output path]')
        return

    args = {
        'zip_path': argv[1],
        'changelog_path': argv[2],
        'download_link': argv[3],
        'output_path': argv[4],
    }

    zip_file = zipfile.PyZipFile(args['zip_path'])
    json_file, json_data, changelog = None, None, None
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
    json_data['DownloadLinkInstall'] = args['download_link']
    json_data['DownloadLinkTesting'] = args['download_link']
    json_data['DownloadLinkUpdate'] = args['download_link']
    json_data['IsHide'] = False
    json_data['IsTestingExclusive'] = False

    # json_data['IsTestingExclusive'] = True
    # json_data['TestingAssemblyVersion'] = json_data['AssemblyVersion']
    # json_data['TestingDalamudApiLevel'] = json_data['DalamudApiLevel']
    # json_data['AssemblyVersion'] = '1.28.4.0'
    # json_data['DalamudApiLevel'] = 9

    json_data['LastUpdate'] = int(datetime(*json_file.date_time).timestamp())
    if changelog is not None and len(changelog) > 0:
        json_data['Changelog'] = changelog

    with open(args['output_path'], 'w') as f:
        json.dump([json_data], f, indent=4, sort_keys=True)

    print(f'wrote to {args["output_path"]}')


if __name__ == '__main__':
    main()
