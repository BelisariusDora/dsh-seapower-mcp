"""Create an Alpha deployment ZIP from source allowlists, never live runtime data."""
import argparse,hashlib,json,posixpath,re,shutil,subprocess,tempfile,zipfile
from pathlib import Path,PurePosixPath

def package(output,version):
 root=Path(__file__).resolve().parent
 output=Path(output).resolve();output.mkdir(parents=True,exist_ok=True)
 game=root.parents[1]
 mapping=json.loads((root/'deployment/modules.json').read_text('utf-8'))
 harness={'HARNESS.md':'README.md','DSH.md':'DSH/README.md','CODEX_DEPLOY.md':'Codex/README.md',
  'harness_link.js':'DSH/harness_link.js','dsh_tailer.js':'DSH/dsh_tailer.js',
  'dsh_ticket_plugin.mjs':'DSH/dsh_ticket_plugin.mjs','dsh_ticket_notifications.mjs':'DSH/dsh_ticket_notifications.mjs',
  'codex_link.js':'Codex/codex_link.js','prompts/onceopen.md':'DSH/prompts/onceopen.md',
  'ticket_runtime.js':'DSH/legacy/ticket_runtime.js','ticket_store.js':'DSH/legacy/ticket_store.js',
  'legacy/codex_link.js':'Codex/legacy/codex_link.js','legacy/codex_desktop.ps1':'Codex/legacy/codex_desktop.ps1',
  'legacy/codex.target.example.json':'Codex/legacy/codex.target.example.json'}
 docs={'deployment_layout.md':'MODULES.md','tickets_deployment.md':'harness/DSH/TICKETS.md',
  'ticket_results_ui.md':'harness/TICKET_DETAILS.md','ticket_notifications.md':'harness/TICKET_NOTIFICATIONS.md',
  'localization.md':'harness/LOCALIZATION.md','settings_page.md':'harness/SETTINGS.md','main_agent_mode.md':'harness/MAIN_AGENT_MODE.md','DEPLOY.md':'README.md'}
 identities={**mapping,**{'comms/'+s:'harness/'+d for s,d in harness.items()}}
 identities.update({'doc/'+p.name:'docs/'+p.name for p in (root/'doc').glob('*.md')})
 def render(source,destination):
  text=(root/source).read_text('utf-8')
  def link(match):
   label,target=match.group(1),match.group(2)
   if re.match(r'\w+://',target) or target.startswith('#'):return match.group(0)
   path,sep,anchor=target.partition('#');resolved=posixpath.normpath(posixpath.join(posixpath.dirname(source),path))
   mapped=identities.get(resolved)
   if mapped:return '['+label+']('+posixpath.relpath(mapped,posixpath.dirname(destination) or '.')+(sep+anchor if sep else '')+')'
   return match.group(0)
  return re.sub(r'(?<!!)\[([^\]]+)\]\(([^)]+)\)',link,text)
 commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip()
 with tempfile.TemporaryDirectory(prefix='sp-alpha-') as temp:
  mod=Path(temp)/'dsh-spmcp';mod.mkdir()
  for source,dest in mapping.items():
   path=mod/dest;path.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(root/source,path)
  for source,dest in [('deployment/launch_mcp.py','mcp_server.py'),('deployment/launch_tickets.py','ticket_scheduler.py'),
      ('build/SeaPowerAgentBridge.dll','SeaPowerAgentBridge.dll'),('build/SeaPowerAgentBridge.build','SeaPowerAgentBridge.build'),('_info.ini','_info.ini'),('LICENSE','LICENSE'),('persona.md','persona.md')]:
   shutil.copyfile(root/source,mod/dest)
  shutil.copyfile(game/'Sea Power_Data/Managed/Newtonsoft.Json.dll',mod/'Newtonsoft.Json.dll')
  for folder in ('portrait','language_cn','language_en'):
   shutil.copytree(root/'deployment/assets'/folder,mod/folder)
  for source,dest in harness.items():
   path=mod/'harness'/dest;path.parent.mkdir(parents=True,exist_ok=True)
   if path.suffix=='.md':path.write_text(render('comms/'+source,'harness/'+dest),encoding='utf-8')
   else:shutil.copyfile(root/'comms'/source,path)
  path=mod/'harness/Codex/prompts/onceopen.md';path.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(root/'comms/prompts/onceopen.md',path)
  for p in (root/'doc').glob('*.md'):
   path=mod/'docs'/p.name;path.parent.mkdir(parents=True,exist_ok=True);path.write_text(render('doc/'+p.name,'docs/'+p.name),encoding='utf-8')
  for source,dest in docs.items():
   path=mod/dest;path.parent.mkdir(parents=True,exist_ok=True);path.write_text(render('doc/'+source,dest),encoding='utf-8')
  (mod/'comms').mkdir()
  (mod/'setting.ini').write_text('[AgentInspector]\nToggleKey=N\nLanguage=\nShotSaveFull=false\n\n[MCP]\nCompactReplies=false\n\n[Tickets]\nMainAgentOnly=false\n',encoding='utf-8')
  digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
  (mod/'deploy_sha.txt').write_text(digest(mod/'SeaPowerAgentBridge.dll').upper(),encoding='ascii')
  files={p.relative_to(mod).as_posix():digest(p) for p in sorted(mod.rglob('*')) if p.is_file()}
  assert not any('anchorchain' in n.lower() or '__pycache__' in n or n.startswith('comms/') for n in files)
  assert not any(n.endswith(('.log','.jsonl','.token','.pyc')) for n in files)
  manifest={'version':version,'tag':'v'+version,'sourceCommit':commit,'build':(mod/'SeaPowerAgentBridge.build').read_text().strip(),'files':files}
  (mod/'release-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
  archive=output/('dsh-spmcp-v'+version+'.zip')
  if archive.exists():raise FileExistsError(archive)
  with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
   z.writestr('dsh-spmcp/comms/','')
   for p in sorted(mod.rglob('*')):
    if p.is_file():z.write(p,'dsh-spmcp/'+p.relative_to(mod).as_posix())
  checksum=archive.with_suffix('.zip.sha256');checksum.write_text(digest(archive)+'  '+archive.name+'\n',encoding='ascii')
  print(json.dumps({'archive':str(archive),'sha256':digest(archive),'files':len(files)+1,'sourceCommit':commit}))

if __name__=='__main__':
 parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',default='dist');parser.add_argument('--version',default='1.1.0-alpha');a=parser.parse_args();package(a.output,a.version)
