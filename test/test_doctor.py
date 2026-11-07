import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location('harness_doctor', Path(__file__).resolve().parents[1] / 'comms/doctor.py')
doctor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(doctor)


class DoctorTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def write(self, relative, content):
        file = self.root / relative
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(content, encoding='utf-8')
        return file

    def test_missing_and_empty_files_fail(self):
        self.write('empty', '')
        with self.assertRaisesRegex(ValueError, 'empty, absent'):
            doctor.file_check(self.root, ['empty', 'absent'])

    def test_prompt_requires_clarification_rule(self):
        file = self.write('persona.md', '普通风格说明')
        with self.assertRaises(ValueError):
            doctor.prompt_check(file)

    def test_live_revision_mismatch_fails(self):
        self.write('harness/DSH/dsh_ticket_plugin.mjs', 'export const revision = "new";')
        self.write('comms/run/tickets/auth.token', 'x' * 32)
        with patch.object(doctor, 'get_json', return_value={'ok': True, 'revision': 'old'}):
            with self.assertRaisesRegex(ValueError, 'revision mismatch'):
                doctor.dsh_check(self.root, 19389)

    def test_bridge_rejects_stale_dll(self):
        with patch.object(doctor, 'get_json', return_value={'ok': True, 'data': {'build': {'staleDll': True}}}):
            with self.assertRaisesRegex(ValueError, 'stale'):
                doctor.bridge_check(19080)

    def test_stale_snapshot_fails(self):
        file = self.write('state.json', '{}')
        with patch.object(doctor.time, 'time', return_value=file.stat().st_mtime + 61):
            with self.assertRaisesRegex(ValueError, 'older'):
                doctor.recent_snapshot(file)

    def test_sender_rejects_unmanaged_mode(self):
        self.write('comms/run/link.status.json', '{"mode":"reuse"}')
        with self.assertRaisesRegex(ValueError, 'game-world'):
            doctor.dsh_sender_check(self.root)

    def test_checks_never_promote_manual_gates_to_pass(self):
        output = io.StringIO()
        with patch.object(sys, 'argv', ['doctor.py', '--root', str(self.root), '--backend', 'mcp', '--json']), \
             patch.object(doctor, 'file_check', return_value='present'), \
             patch.object(doctor, 'mcp_check', return_value='catalog present'), \
             patch.object(doctor, 'get_json') as network, contextlib.redirect_stdout(output):
            code = doctor.main()
        report = json.loads(output.getvalue())
        self.assertEqual(code, 2)
        self.assertEqual(report['installationStatus'], 'NOT_FULLY_VERIFIED')
        self.assertEqual(report['checks'][-1]['status'], 'NOT_CHECKED')
        network.assert_not_called()

    def test_failure_is_reported_without_stopping_later_checks(self):
        output = io.StringIO()
        with patch.object(sys, 'argv', ['doctor.py', '--root', str(self.root), '--backend', 'mcp', '--json']), \
             contextlib.redirect_stdout(output):
            code = doctor.main()
        report = json.loads(output.getvalue())
        self.assertEqual(code, 1)
        self.assertEqual(report['installationStatus'], 'FAILED')
        self.assertEqual(report['checks'][-1]['check'], 'game.roundtrip')


if __name__ == '__main__':
    unittest.main()
