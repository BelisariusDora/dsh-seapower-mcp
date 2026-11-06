"""Read-only ticket presentation built from host receipts, never model assertions."""
import json
from pathlib import Path

def resource_root():
    from runtime_layout import configuration_root
    root = configuration_root()
    assets = root / 'deployment/assets'
    return assets if assets.is_dir() else root


EN_FILES = ('language_en/ui.ini', 'localization/en.ini', 'localization/en-US.ini')
ZH_FILES = ('language_cn/ui.ini', 'language_zh/ui.ini', 'localization/cn.ini',
            'localization/zh.ini', 'localization/zh-Hans.ini', 'localization/zh-CN.ini')
_tables = {}


def language_files(root=None):
    root = Path(root) if root is not None else resource_root()
    def select(names):
        return next((root / name for name in names if (root / name).is_file()), None)
    return select(EN_FILES), select(ZH_FILES)


def language_signature(root=None):
    return tuple((str(p), p.stat().st_mtime_ns, p.stat().st_size) if p else None
                 for p in language_files(root))


def read_language(file):
    # Same Key=Value, optional section, comments, BOM and last-wins rules as Strings.cs.
    result = {}
    if file is None:
        return result
    section = ''
    try:
        contents = file.read_text('utf-8-sig')
    except (OSError, UnicodeError):
        return result
    for raw in contents.splitlines():
        line = raw.strip()
        if not line or line.startswith((';', '#')):
            continue
        if line.startswith('[') and line.endswith(']'):
            section = line[1:-1].strip()
            continue
        key, separator, value = line.partition('=')
        if not separator or not key.strip():
            continue
        key, value = key.strip(), value.strip()
        if section:
            result[section + '/' + key] = value
        result[key] = value
    return result


def translator(language, root=None):
    signature = language_signature(root)
    cache_key = str(Path(root) if root is not None else resource_root())
    cached_tables = _tables.get(cache_key)
    if cached_tables is None or cached_tables[0] != signature:
        en_file, zh_file = language_files(root)
        cached_tables = (signature, read_language(en_file), read_language(zh_file))
        _tables[cache_key] = cached_tables
    english, chosen = cached_tables[1], cached_tables[2] if language == 'zh' else cached_tables[1]
    def lookup(key):
        for table in (chosen, english):
            if 'AgentInspector/' + key in table:
                return table['AgentInspector/' + key]
            if key in table:
                return table[key]
        return key
    return lookup


OUTCOMES = {'fulfilled', 'partial', 'blocked', 'needs_input', 'unreported'}
LABELS = {'fulfilled': 'tickets.view.text01', 'partial': 'tickets.view.text02',
          'blocked': 'tickets.view.text03', 'needs_input': 'tickets.view.text04', 'unreported': 'tickets.view.text05'}
STATES = {'queued': 'tickets.view.text06', 'starting': 'tickets.view.text07', 'running': 'tickets.view.text08', 'monitoring': 'tickets.view.text09',
          'completed': 'tickets.view.text10', 'failed': 'tickets.view.text11', 'uncertain': 'tickets.view.text12',
          'cancelled': 'tickets.view.text13', 'timed_out': 'tickets.view.text14', 'blocked': 'tickets.view.text03', 'needs_input': 'tickets.view.text04'}
PREFIXES = ('APIs executed; game effects not independently verified. ',
            'One or more API calls failed. ', 'An API outcome is uncertain; never replay writes. ')


def clean_summary(value):
    for prefix in PREFIXES:
        if value.startswith(prefix):
            return value[len(prefix):]
    return value


def public(value):
    if isinstance(value, dict):
        return {k: ('[redacted]' if any(s in k.lower() for s in ('token', 'authorization', 'password', 'secret'))
                    else public(v)) for k, v in value.items()}
    if isinstance(value, list):
        return [public(v) for v in value]
    return value


def compact(value, limit=700, language="zh"):
    T = translator(language)
    text = json.dumps(public(value), ensure_ascii=False, separators=(',', ':'))
    return text if len(text) <= limit else text[:limit] + T('tickets.view.text15')


def evidence(ticket, records, unavailable=False, language="zh"):
    T = translator(language)
    render = lambda value, limit=700: compact(value,limit,language)
    outcome = ticket.get('taskOutcome', 'unreported')
    status = ticket['status']
    label = T(LABELS.get(outcome, LABELS['unreported']) if status == 'executed' else STATES.get(status, status))
    summary = clean_summary(ticket.get('outcomeReason') or ticket.get('detail', ''))
    confirmed, pending, reads, watch = [], [], [], []
    calls = []
    for row in sorted(records, key=lambda r: (r.get('at', 0), r['call_id'])):
        item = {'api': row.get('name', ''), 'arguments': public(row.get('arguments', {})),
                'status': row.get('status', 'unknown'), 'callId': row['call_id']}
        raw = row.get('receipt') or {}
        data = raw.get('data') or {}
        if isinstance(data, dict):
            keys = ('action', 'executed', 'verified', 'confirmation', 'fired', 'dropped', 'pending',
                    'reason', 'error', 'taskReadback', 'weaponStatus', 'orderText')
            item['receiptSummary'] = public({k: data[k] for k in keys if k in data})
        else:
            item['receiptSummary'] = {}
        calls.append(item)
        validation_only = row.get('name') in {'sp_attack', 'sp_fire'} and row.get('arguments', {}).get('validate_only') is True
        if row.get('readOnly') is True or validation_only:
            reads.append(item)
        elif (row.get('readOnly') is False and row.get('status') == 'succeeded'
              and raw.get('ok') is True and raw.get('uncertain') is not True
              and raw.get('resend') != 'forbidden' and (not isinstance(data, dict) or data.get('executed') is not False)):
            confirmed.append(item)
        else:
            pending.append(item)
        if row.get('name', '').startswith('sp_watch_'):
            # Historical evidence only. A creation receipt is not a current Watch hit.
            watch.append({'api': item['api'], 'arguments': item['arguments'], 'callStatus': item['status'],
                          'observedAt': row.get('finishedAt'), 'evidence': public(data),
                          'error': public(raw.get('error'))})
    # The speed contract predates the generic call ledger; its validated receipt is authoritative.
    if ticket.get('command', {}).get('kind') == 'set_speed' and ticket.get('receipt') and not any(c['api']=='sp_set_speed' for c in confirmed):
        receipt = ticket['receipt']
        if receipt.get('ok') is True and not receipt.get('uncertain'):
            confirmed.append({'api': 'sp_set_speed', 'arguments': ticket['command'], 'status': 'succeeded',
                              'receiptSummary': public(receipt.get('data', {})), 'callId': 'speed-contract'})
    command_summary = (T('tickets.view.text16') % (len(confirmed), len(reads)) if confirmed
                       else T('tickets.view.text17') % len(reads))
    if pending:
        command_summary += T('tickets.view.text18') % len(pending)
    if unavailable:
        command_summary = T('tickets.view.text19')
    watch_summary = T('tickets.view.text20')
    if watch:
        last = watch[-1]
        data = last.get('evidence')
        data = data if isinstance(data, dict) else {}
        events = data.get('events')
        latest = events[-1] if isinstance(events, list) and events and isinstance(events[-1], dict) else data
        last_state = latest.get('state') or latest.get('status') or latest.get('kind')
        boundary = latest.get('reason') or latest.get('error') or last.get('error')
        if boundary:
            watch_summary = T('tickets.view.text21') + ' ' + (str(last_state) + ' · ' if last_state else '') + render(boundary, 180)
        elif last_state:
            watch_summary = T('tickets.view.text22') + ' ' + str(last_state) + ' ' + T('tickets.view.text23')
        elif last['api'] == 'sp_watch_create':
            watch_summary = T('tickets.view.text24')
        else:
            watch_summary = T('tickets.view.text25')
    elif ticket.get('predicate', {}).get('kind') == 'speed_reached':
        watch_summary = T('tickets.view.text26') + ' ' + str(ticket.get('stableSamples', 0)) + T('tickets.view.text27') + ' ' + T(STATES.get(status, status))
    if unavailable:
        watch_summary = T('tickets.view.text28')
    executor=T('tickets.view.text29') if ticket.get('executionMode')=='main' else 'Worker'
    lines = [T('tickets.view.text30') + ' '+executor, T('tickets.view.text31') + ' ' + label, T('tickets.view.text32') + ' ' + status,
             T('tickets.view.text33') + ' ' + (T('tickets.view.text34') if ticket.get('resultVerified') else T('tickets.view.text35')),
             T('tickets.view.text36') + ' ' + (summary or T('tickets.view.text37')),
             executor+' ' + T('tickets.view.text38') + ' ' + (clean_summary(ticket.get('detail', '')) or T('tickets.view.text37')),
             executor+' ' + T('tickets.view.text39') + ' ' + str(ticket.get('workerStatus', '')),
             T('tickets.view.text40') + ' ' + ticket.get('task', ''), T('tickets.view.text41') + ' ' + ticket.get('completion', ''),
             T('tickets.view.text42') + ' ' + command_summary]
    if status == 'executed' and outcome == 'unreported':
        lines.append(T('tickets.view.text43'))
    if outcome in ('fulfilled', 'partial'):
        lines.append(T('tickets.view.text44') + ' '+executor+T('tickets.view.text45'))
    for heading, items in ((T('tickets.view.text46'), confirmed),
                           (T('tickets.view.text47'), pending), (T('tickets.view.text48'), reads)):
        lines.append(heading + '：')
        lines.extend([str(i + 1) + '. ' + c['api'] + ' ' + render(c['arguments']) + '\n' + T('tickets.view.text50') + ' ' + c['status']
                      + (' ' + render(c['receiptSummary']) if c['receiptSummary'] else '')
                      for i, c in enumerate(items)] or [T('tickets.view.text51')])
    lines.append(T('tickets.view.text52'))
    if watch:
        lines.extend(render(w, 2500) for w in watch)
    elif ticket.get('predicate', {}).get('kind') == 'speed_reached':
        lines.append(T('tickets.view.text53') + ' ' + render(ticket['predicate']))
        lines.append(T('tickets.view.text54') + ' ' + str(ticket.get('stableSamples', 0)) + '/2')
        if ticket.get('observation'):
            lines.append(T('tickets.view.text56') + ' ' + render(ticket['observation']))
        if status in ('timed_out', 'cancelled', 'failed', 'uncertain'):
            lines.append(T('tickets.view.text57') + ' ' + summary)
    else:
        lines.append(T('tickets.view.text58'))
    lines.extend([T('tickets.view.text59') + ' ' + ticket['id'], T('tickets.view.text60') + ' ' + str(ticket.get('worldKey', '')),
                  T('tickets.view.text61') + ' ' + str(ticket.get('updatedAt', '')),
                  T('tickets.view.text62') + ' ' + str(ticket.get('createdAt', '')) + ' / ' + str(ticket.get('deadlineAt', ''))])
    return {'resultLabel': label, 'taskOutcome': outcome, 'summary': summary,
            'commandSummary': command_summary, 'watchSummary': watch_summary, 'confirmedCommands': confirmed, 'unconfirmedWrites': pending,
            'readCount': len(reads), 'watchEvidence': watch, 'detailText': '\n\n'.join(lines),
            'evidenceUnavailable': unavailable}


def cached(ticket, root, cache, language="zh"):
    directory = Path(root) / 'api_calls' / ticket['id']
    try:
        files = sorted(directory.glob('*.json'))
        signature = (language, language_signature(), ticket['revision'], tuple((p.name, p.stat().st_mtime_ns, p.stat().st_size) for p in files))
        if ticket['id'] in cache and cache[ticket['id']][0] == signature:
            return cache[ticket['id']][1]
        view = evidence(ticket, [json.loads(p.read_text('utf-8')) for p in files], language=language)
        cache[ticket['id']] = (signature, view)
        return view
    except (OSError, ValueError, KeyError, TypeError):
        return evidence(ticket, [], unavailable=True, language=language)
