from sys import argv
from datetime import datetime
import zipfile
import json


def main():
    if len(argv) < 4:
        print('make_repo.py [latest.zip] [download link] [output path]')
        return

    args = {
        'zip_path': argv[1],
        'download_link': argv[2],
        'output_path': argv[3],
    }

    zip = zipfile.PyZipFile(args['zip_path'])
    json_file = None
    for f in zip.filelist:
        if f.filename.endswith('.json'):
            json_file = f
            with zip.open(json_file, 'r') as f:
                json_text = f.read()
            json_data = json.loads(json_text)
            if 'DalamudApiLevel' in json_data:
                break
            json_file = None
    if json_file is None:
        return
    json_data['DownloadLinkInstall'] = args['download_link']
    json_data['DownloadLinkTesting'] = args['download_link']
    json_data['DownloadLinkUpdate'] = args['download_link']
    json_data['IsHide'] = False
    json_data['IsTestingExclusive'] = False
    json_data['LastUpdate'] = int(datetime(*json_file.date_time).timestamp())

    with open(args['output_path'], 'w') as f:
        json.dump([json_data], f, indent=4, sort_keys=True)

    print(f'wrote to {args["output_path"]}')


if __name__ == '__main__':
    main()
