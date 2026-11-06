"""Deployment layout paths; flat source checkout remains supported."""
import importlib.util
from pathlib import Path
import runpy
import sys


def configuration_root():
    directory=Path(__file__).resolve().parent
    return directory.parent if directory.name=='modules' else directory


def deployment_root():
    directory=Path(__file__).resolve().parent
    if directory.name=='modules':
        return directory.parent
    return directory.parents[1]/'Sea Power_Data'/'StreamingAssets'/'dsh-spmcp'


def activate():
    root=configuration_root()
    directories=[root/'modules/mcp',root/'modules/tickets',root/'modules/api',
                 root/'harness/DSH',root/'harness/Codex',root/'modules']
    for directory in reversed(directories):
        if directory.is_dir() and str(directory) not in sys.path:
            sys.path.insert(0,str(directory))


def run_entry(relative,namespace):
    activate()
    target=Path(__file__).resolve().parent/relative
    if namespace['__name__']=='__main__':
        runpy.run_path(str(target),run_name='__main__')
    else:
        name=namespace['__name__']
        spec=importlib.util.spec_from_file_location(name,target)
        module=importlib.util.module_from_spec(spec)
        sys.modules[name]=module
        spec.loader.exec_module(module)
        namespace.update(vars(module))
