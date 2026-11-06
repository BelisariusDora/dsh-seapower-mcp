"""Small on-demand catalog generated from the existing MCP registry."""
import copy
import json
from pathlib import Path

CONTROL = {'sp_ticket_create','sp_ticket_report','sp_ticket_destroy','sp_ticket_list',
           'sp_comms_send','sp_comms_window','sp_agent_panel'}
READ_ONLY = set('sp_ping sp_session sp_session_fleet sp_state sp_units sp_unit sp_ammo sp_weapons sp_deck sp_sub sp_vertical_state sp_tracks sp_tracks_brief sp_waypoints sp_taskforces sp_validate_attack sp_airborne sp_decktasks sp_screenshot sp_map_view sp_map_units sp_map_to_geo sp_plot_measure sp_plot_drawings sp_watch_list sp_watch_get sp_watch_events sp_comms_state sp_comms_page'.split())
ALIASES = {'sp_speed':'sp_set_speed'}
TOPICS = {'plot':'绘图 地图 画图 标记 圈 多边形 文字 路线 擦除 map draw drawing marker polygon text ring route erase',
          'deck':'甲板 航母 飞机 起飞 返航 挂载 launch aircraft airborne carrier deck recover rearm',
          'units':'单位 编队 舰艇 航速 航向 移动 武器 雷达 声呐 状态 speed course move attack radar sonar unit weapons',
          'watch':'监视 条件 事件 watch event condition','time':'时间 暂停 倍率 time pause compression',
          'inspect':'检查 截图 反射 调试 raw inspect screenshot','comms':'通信 记录 会话 comms session'}
NOTES = {
 'sp_launch_aircraft':['loadout is REQUIRED by the Bridge. Select exact aircraft fileName and available loadout from sp_deck.','Use require_exact_loadout=true when a specific loadout is authorized. Submission does not prove airborne; read sp_airborne later.'],
 'sp_airborne':['Reads actual child-aircraft association. Existing aircraft are not proof of a new launch; compare pre-command IDs.'],
 'sp_watch_create':['A field must actually exist; field_unreadable is a failure, never a satisfied condition. Keep returned cursor/world_generation.'],
 'sp_plot_erase':['Destructive drawing operation. Scope all_mine/indices to the player authorization; never clear unrelated drawings.'],
 'sp_time':['Only change pause/compression when authorized. Workers must not extend simulation time to monitor their own results.'],
 'sp_raw':['Advanced fixed-host Bridge access only. Ticket and comms control routes cannot be called through raw.'],
 'sp_recover_aircraft':['Single-aircraft recall must not silently broaden to the whole airgroup. Follow actual deck task/aircraft evidence.']}
WORKFLOWS = {
 'plot':['sp_map_view or sp_map_units -> sp_plot_marker/ring/polygon/text -> sp_plot_drawings', 'Use geographical coordinates and actual tool schema. Do not inspect source files.'],
 'deck':['sp_deck -> exact aircraft fileName/loadout -> sp_launch_aircraft -> record submitted receipt', 'Completion of launch requires later game observations; generic execution alone does not verify airborne.'],
 'units':['sp_session_fleet after loading -> sp_unit -> authorized command', 'Unit IDs are valid only in the current world.'],
 'watch':['sp_watch_create -> persist watch_id/world_generation/event_cursor -> sp_watch_events', 'Worker exits after registration. Persistent event consumption belongs to the scheduler, not an LLM wait loop.']}

def registry():
    import mcp_server
    return mcp_server.TOOLS_BY_NAME

def business(name):
    return name not in CONTROL and not name.startswith(('sp_api_', 'sp_main_', '_sp_main_'))

def domain(name):
    if name.startswith(('sp_plot_','sp_map_')):return 'plot'
    if name.startswith('sp_watch_'):return 'watch'
    if name in {'sp_deck','sp_launch_aircraft','sp_airborne','sp_decktasks','sp_recover_aircraft','sp_abort_task','sp_rearm'}:return 'deck'
    if name == 'sp_time':return 'time'
    if name in {'sp_raw','sp_screenshot','sp_inspector_window','sp_agent_panel'}:return 'inspect'
    if name.startswith('sp_comms_') or name=='sp_session':return 'comms'
    return 'units'

def find(args, tools=None):
    tools=tools or registry()
    query=str(args.get('query','')).lower().strip()
    selected=args.get('domain')
    limit=max(1,min(20,int(args.get('limit',8))))
    hits=[]
    for name,tool in tools.items():
        if name.startswith('sp_api_'):continue
        group=domain(name)
        if selected and selected!=group:continue
        haystack=(name+' '+tool['description']+' '+TOPICS.get(group,'')).lower()
        terms=query.split()
        if terms and not any(term in haystack for term in terms):continue
        score=sum((5 if term in name else 1) for term in terms if term in haystack)
        schema=tool['inputSchema']
        hits.append((score,{'name':name,'domain':group,'workerAllowed':business(name),
            'readOnly':name in READ_ONLY,'required':schema.get('required',[]),
            'summary':tool['description'][:220],'notes':NOTES.get(name,[])}))
    hits.sort(key=lambda row:(-row[0],row[1]['name']))
    return {'ok':True,'count':len(hits),'tools':[row[1] for row in hits[:limit]],
            'domains':list(TOPICS),'workflow':WORKFLOWS.get(selected,[]),
            'rule':'Describe selected tools, then call with ticket capability. Never infer game completion from a write receipt.'}

def describe(args, tools=None):
    tools=tools or registry()
    names=args.get('names')
    if not isinstance(names,list) or not 1<=len(names)<=8:raise ValueError('names must contain 1..8 tools')
    result=[]
    for requested in names:
        name=ALIASES.get(requested,requested)
        if name not in tools or name.startswith('sp_api_'):raise ValueError('Unknown business tool: '+str(requested))
        tool=tools[name]
        result.append({'name':name,'description':tool['description'],'inputSchema':copy.deepcopy(tool['inputSchema']),
                       'workerAllowed':business(name),'readOnly':name in READ_ONLY,
                       'notes':NOTES.get(name,[]),'workflow':WORKFLOWS.get(domain(name),[])})
    return {'ok':True,'tools':result}

def helper_specs():
    return json.loads(Path(__file__).with_name('api_tools.json').read_text(encoding='utf-8'))


def is_read_only(name, arguments):
    if name in READ_ONLY:
        return True
    if name == 'sp_raw':
        return str(arguments.get('method','GET')).upper() == 'GET'
    if name == 'sp_plot_route':
        return arguments.get('points') is None
    if name == 'sp_inspector_window':
        return arguments.get('op','state') == 'state'
    return False