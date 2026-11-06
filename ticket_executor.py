"""Shared executor failure contract, independent of any Harness.

Adapters expose context(world), start(ticket), status(ticket), stop(ticket).
Context and worker handles are opaque to the scheduler. A true stop result must
mean the native worker has exited; issuing a stop request alone is insufficient.
"""


class ExecutorUnavailable(ValueError):
    """No native start was attempted; safe to leave the ticket queued."""


import ctypes
import os


def process_alive(pid):
    if type(pid) is not int or pid <= 0:
        raise ValueError("Invalid runtime lock PID")
    if os.name == "nt":
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.OpenProcess.argtypes = (ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32)
        kernel.GetExitCodeProcess.argtypes = (ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32))
        kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
        handle = kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            if ctypes.get_last_error() == 87:
                return False
            raise OSError("Cannot establish runtime lock owner")
        try:
            code = ctypes.c_uint32()
            if not kernel.GetExitCodeProcess(handle, ctypes.byref(code)):
                raise OSError("Cannot read runtime process state")
            return code.value == 259
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False



class ExecutorRouter:
    """Route each persisted ticket by its executor context; one shared scheduler."""
    def __init__(self, adapters, default='dsh'):
        self.adapters, self.default = adapters, default

    def select_context(self, world, harness=None):
        harness = harness or self.default
        if harness not in self.adapters:
            raise ValueError('Unknown executor: ' + str(harness))
        return self.adapters[harness].context(world) or {"harness": harness, "pending": True}

    def context(self, world):
        return self.select_context(world)

    def resolve_context(self, world, ticket):
        context = ticket.get('executorContext')
        if context and not context.get('pending'):
            return context
        harness = (context or {}).get('harness', 'dsh')
        return self.adapters[harness].context(world)

    def adapter(self, ticket):
        context = ticket.get('executorContext') or {}
        harness = context.get('harness') or (ticket.get('workerHandle') or {}).get('harness') or 'dsh'
        if harness not in self.adapters:
            raise ValueError('Unknown persisted executor')
        return self.adapters[harness]

    def start(self, ticket):
        return self.adapter(ticket).start(ticket)

    def status(self, ticket):
        return self.adapter(ticket).status(ticket)

    def stop(self, ticket):
        return self.adapter(ticket).stop(ticket)
