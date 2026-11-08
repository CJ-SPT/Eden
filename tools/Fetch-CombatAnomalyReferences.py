"""Fetch the exact public wiki revisions recorded in Eden's anomaly encounter facts."""
import argparse
import json
import pathlib
import urllib.parse
import urllib.request


def fetch(facts_path, output):
    facts = json.loads(facts_path.read_text(encoding='utf-8'))
    pages = {e['name']: {'missing': True} for e in facts['encounters'] if e.get('reconstructed')}
    revisions = {e['revision'] for e in facts['encounters'] if not e.get('reconstructed')}
    ordered = sorted(revisions)
    for start in range(0, len(ordered), 40):
        query = urllib.parse.urlencode(dict(action='query', prop='revisions', rvprop='ids|content',
                                           rvslots='main', revids='|'.join(map(str, ordered[start:start + 40])), format='json'))
        request = urllib.request.Request('https://wiki.eveuniversity.org/api.php?' + query,
                                         headers={'User-Agent': 'Eden anomaly reference audit'})
        with urllib.request.urlopen(request, timeout=60) as response:
            payload = json.load(response)
        if payload.get('error'):
            raise ValueError(payload['error'])
        for page in payload['query']['pages'].values():
            revision = page['revisions'][0]
            pages[page['title']] = dict(revision=revision['revid'], text=revision['slots']['main']['*'])
    if {e['name'] for e in facts['encounters']} != set(pages):
        raise ValueError('The server did not return every recorded encounter revision')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(pages, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print('Fetched', len(revisions), 'exact wiki revisions')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--facts', type=pathlib.Path,
                        default=pathlib.Path('src/Eden.World/Exploration/combat-anomaly-encounters-3662168.json'))
    parser.add_argument('--output', required=True, type=pathlib.Path)
    args = parser.parse_args()
    fetch(args.facts, args.output)
