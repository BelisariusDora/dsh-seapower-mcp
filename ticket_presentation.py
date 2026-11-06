"""Read-only ticket presentation built from host receipts, never model assertions."""
import json
from pathlib import Path

OUTCOMES = {'fulfilled', 'partial', 'blocked', 'needs_input', 'unreported'}
LABELS = {'fulfilled': '已处理 · 效果未验证', 'partial': '部分处理',
          'blocked': '受阻', 'needs_input': '需要澄清', 'unreported': '调用已记录 · 结果未报告'}
STATES = {'queued': '排队中', 'starting': '启动中', 'running': '执行中', 'monitoring': '监视中',
          'completed': '已验证达标', 'failed': '失败', 'uncertain': '结果不明 · 禁止重发',
          'cancelled': '已取消', 'timed_out': '已超时', 'blocked': '受阻', 'needs_input': '需要澄清'}
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


def compact(value, limit=700):
    text = json.dumps(public(value), ensure_ascii=False, separators=(',', ':'))
    return text if len(text) <= limit else text[:limit] + '…（长参数已缩略，宿主原始账本保留）'


def evidence(ticket, records, unavailable=False):
    outcome = ticket.get('taskOutcome', 'unreported')
    status = ticket['status']
    label = LABELS.get(outcome, LABELS['unreported']) if status == 'executed' else STATES.get(status, status)
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
    command_summary = ('已提交 %d 项写入；读取 %d 项' % (len(confirmed), len(reads)) if confirmed
                       else '未记录游戏写入；读取 %d 项' % len(reads))
    if pending:
        command_summary += '；%d 项写入失败或结果不明' % len(pending)
    if unavailable:
        command_summary = '调用账本暂不可读，不能据此判断是否下令'
    watch_summary = 'Watch：无本任务记录'
    if watch:
        last = watch[-1]
        data = last.get('evidence')
        data = data if isinstance(data, dict) else {}
        events = data.get('events')
        latest = events[-1] if isinstance(events, list) and events and isinstance(events[-1], dict) else data
        last_state = latest.get('state') or latest.get('status') or latest.get('kind')
        boundary = latest.get('reason') or latest.get('error') or last.get('error')
        if boundary:
            watch_summary = 'Watch 边界：' + (str(last_state) + ' · ' if last_state else '') + compact(boundary, 180)
        elif last_state:
            watch_summary = 'Watch 最近记录：' + str(last_state) + '（非实时）'
        elif last['api'] == 'sp_watch_create':
            watch_summary = 'Watch：已记录创建，命中未确认'
        else:
            watch_summary = 'Watch：已记录查询，详情见证据'
    elif ticket.get('predicate', {}).get('kind') == 'speed_reached':
        watch_summary = '航速判据：' + str(ticket.get('stableSamples', 0)) + '/2 样本 · ' + STATES.get(status, status)
    if unavailable:
        watch_summary = 'Watch：账本不可读，证据未知'
    executor='主 Agent' if ticket.get('executionMode')=='main' else 'Worker'
    lines = ['执行者：'+executor, '任务结果：' + label, '调用状态：' + status,
             '验证：' + ('已由宿主真值观测确认' if ticket.get('resultVerified') else '未独立验证游戏效果'),
             '结果摘要：' + (summary or '尚无回报'),
             executor+' 完整回报：' + (clean_summary(ticket.get('detail', '')) or '尚无回报'),
             executor+' 状态：' + str(ticket.get('workerStatus', '')),
             '目标：' + ticket.get('task', ''), '预期：' + ticket.get('completion', ''),
             '执行证据：' + command_summary]
    if status == 'executed' and outcome == 'unreported':
        lines.append('兼容说明：旧执行器未结构化报告任务结果；调用成功不等于任务已落实。')
    if outcome in ('fulfilled', 'partial'):
        lines.append('任务结果由'+executor+'报告，实际调用以下方宿主账本为准。')
    for heading, items in (('已提交命令（不等于已发射或已达标）', confirmed),
                           ('未确认的写入（禁止盲目重发）', pending), ('读取记录', reads)):
        lines.append(heading + '：')
        lines.extend([str(i + 1) + '. ' + c['api'] + ' ' + compact(c['arguments']) + '\n回执：' + c['status']
                      + (' ' + compact(c['receiptSummary']) if c['receiptSummary'] else '')
                      for i, c in enumerate(items)] or ['无'])
    lines.append('Watch / 判据（仅显示已记录证据，不代表当前仍有效）：')
    if watch:
        lines.extend(compact(w, 2500) for w in watch)
    elif ticket.get('predicate', {}).get('kind') == 'speed_reached':
        lines.append('调度器航速判据：' + compact(ticket['predicate']))
        lines.append('连续达标样本：' + str(ticket.get('stableSamples', 0)) + '/2')
        if ticket.get('observation'):
            lines.append('最近观测：' + compact(ticket['observation']))
        if status in ('timed_out', 'cancelled', 'failed', 'uncertain'):
            lines.append('监视已终止或受阻：' + summary)
    else:
        lines.append('本任务无已记录的 Watch 调用或自动真值完成判据。')
    lines.extend(['工单 ID：' + ticket['id'], '对局：' + str(ticket.get('worldKey', '')),
                  '更新时间（Unix 毫秒）：' + str(ticket.get('updatedAt', '')),
                  '创建/截止（Unix 毫秒）：' + str(ticket.get('createdAt', '')) + ' / ' + str(ticket.get('deadlineAt', ''))])
    return {'resultLabel': label, 'taskOutcome': outcome, 'summary': summary,
            'commandSummary': command_summary, 'watchSummary': watch_summary, 'confirmedCommands': confirmed, 'unconfirmedWrites': pending,
            'readCount': len(reads), 'watchEvidence': watch, 'detailText': '\n\n'.join(lines),
            'evidenceUnavailable': unavailable}


def cached(ticket, root, cache):
    directory = Path(root) / 'api_calls' / ticket['id']
    try:
        files = sorted(directory.glob('*.json'))
        signature = (ticket['revision'], tuple((p.name, p.stat().st_mtime_ns, p.stat().st_size) for p in files))
        if ticket['id'] in cache and cache[ticket['id']][0] == signature:
            return cache[ticket['id']][1]
        view = evidence(ticket, [json.loads(p.read_text('utf-8')) for p in files])
        cache[ticket['id']] = (signature, view)
        return view
    except (OSError, ValueError, KeyError, TypeError):
        return evidence(ticket, [], unavailable=True)
