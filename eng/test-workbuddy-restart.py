"""重启归属回归；不连接 Docker，不重启真实容器。"""
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location("verify", Path(__file__).with_name("workbuddy-sonnetdb-verify.py"))
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)


class RestartOwnershipTests(unittest.TestCase):
    def test_port_and_image_ownership_before_restart(self):
        cases = [("127.0.0.1", "18581", "tcp", True, "sonnetdb", False),
                 ("127.0.0.1", "18580", "tcp", True, "sonnetdb", True),
                 ("127.0.0.1", "18580", "tcp", False, "sonnetdb", True),
                 ("0.0.0.0", "18580", "tcp", True, "sonnetdb", False),
                 ("127.0.0.1", "18580", "udp", True, "sonnetdb", False),
                 ("127.0.0.1", "18580", "tcp", True, "redis", False)]
        for ip, port, protocol, tagged, image, accepted in cases:
            with self.subTest(ip=ip, port=port, protocol=protocol, tagged=tagged, image=image):
                container_id = "a" * 64
                record = {"Id": container_id, "Config": {"Image": "iotsharp/" + image + ":5.0.0",
                    "Labels": {"sonnetdb.purpose": "workbuddy-verification" if tagged else "other"}},
                    "NetworkSettings": {"Ports": {"5080/" + protocol: [{"HostIp": ip, "HostPort": port}]}}}
                verifier = VERIFY.Verification.__new__(VERIFY.Verification)
                verifier.args = SimpleNamespace(restart_container="fixture", base_url="http://127.0.0.1:18580")
                verifier.http, verifier.readbacks, verifier.report = Mock(), [], {}
                failures = []

                def run_check(model, transport, name, action):
                    try:
                        action()
                        return True
                    except VERIFY.CheckFailure as exc:
                        failures.append(exc.code)
                        return False

                verifier.run_check = run_check
                inspected = SimpleNamespace(returncode=0, stdout=json.dumps([record]).encode())
                with patch.object(VERIFY.subprocess, "run", side_effect=[inspected, SimpleNamespace(returncode=0)]) as run:
                    verifier.persistence()
                self.assertEqual("pass" if accepted else "fail", verifier.report["persistence"]["status"])
                self.assertEqual(2 if accepted else 1, run.call_count)
                if accepted:
                    self.assertEqual([], failures)
                    self.assertEqual(["docker", "restart", "--time", "15", container_id], run.call_args.args[0])
                    verifier.http.request.assert_called_once_with("GET", "/healthz/live")
                else:
                    self.assertEqual(1, len(failures))
                    verifier.http.request.assert_not_called()


if __name__ == "__main__":
    unittest.main()
