"""Import factual blue-loot quantities; bind the source index names to native NPC IDs.

Some WHDBX detail headings repeat another NPC name. The canonical index link is used,
reconciled with build-3662168 item names; detail pages supply only the loot quantities.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import html
import json
from pathlib import Path
import re
import sqlite3
import urllib.request


def text(raw):
    return ' '.join(html.unescape(re.sub('<[^>]*>', ' ', raw)).split())


def main(args):
    root = 'https://evewh.ru/sleepers/'
    index = urllib.request.urlopen(root, timeout=30).read().decode()
    links = {int(i): text(part) for i, part in re.findall(
        r'<a[^>]*href="/sleepers/(\d+)/"[^>]*>(.*?)</a>', index, re.S)}
    with sqlite3.connect(args.catalog.resolve().as_uri() + '?mode=ro', uri=True) as db:
        types = {}
        for type_id, name, category in db.execute('SELECT t.Id,t.Name,g.CategoryId FROM ItemType t '
                                                'JOIN ItemGroup g ON g.Id=t.GroupId ORDER BY t.Id'):
            if category == 11:
                types.setdefault(name, type_id)
        loot_types = {name: type_id for type_id, name in db.execute('SELECT Id,Name FROM ItemType WHERE Id BETWEEN 30744 AND 30747')}

        def fetch(pair):
            key, name = pair
            url = root + str(key) + '/'
            raw = text(urllib.request.urlopen(url, timeout=30).read().decode())
            section = raw[raw.index('Loot:'):] if 'Loot:' in raw else ''
            pattern = r'(\d+) x (Ancient Coordinates Database|Neural Network Analyzer|Sleeper Data Library|Sleeper Drone AI Nexus)'
            drops = [dict(typeId=loot_types[label], quantity=int(count)) for count, label in re.findall(pattern, section) if int(count)]
            return dict(typeId=types[name], name=name, source=url, drops=drops)

        with ThreadPoolExecutor(max_workers=4) as pool:
            rows = list(pool.map(fetch, sorted(links.items())))
        args.output.write_text(json.dumps(dict(npcs=rows), indent=2) + '\n', encoding='utf-8')
        print('Exported', len(rows), 'native NPC blue-loot records')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    main(parser.parse_args())
