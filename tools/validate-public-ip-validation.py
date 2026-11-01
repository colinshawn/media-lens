#!/usr/bin/env python3
"""Check public-IP runtime configuration in a built image using synthetic inputs only."""

import argparse
import contextlib
import datetime
import json
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", default="media-lens:public-ip-validation")
    parser.add_argument("--output", type=Path, default=Path("docs/validation/public-ip-validation.json"))
    options = parser.parse_args()
    checks = []
    secret = "synthetic-public-ip-container-secret-1234567890"
    invalid = "synthetic-invalid-boolean-do-not-log"
    key = "MediaLens:Http:ValidatePublicIp"
    native = "MediaLens__Http__ValidatePublicIp"
    warning = "Public IP validation is disabled"
    http = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def docker(*arguments, timeout=30):
        result = subprocess.run(["docker", *arguments], capture_output=True, text=True, timeout=timeout)
        if result.returncode:
            raise RuntimeError(f"Docker {arguments[0]} failed: {result.stderr.strip()}")
        output = result.stdout + result.stderr if arguments[0] == "logs" else result.stdout
        return output.strip()

    def check(name, condition):
        if not condition:
            raise AssertionError(name)
        checks.append({"name": name, "passed": True})

    @contextlib.contextmanager
    def container(environment, arguments=(), publish=False):
        command = ["create", "--name", "medialens-public-ip-check-" + uuid.uuid4().hex,
                   "--read-only", "--tmpfs", "/tmp:rw,nosuid,size=32m", "-e", "SECRET_TOKEN=" + secret]
        if publish:
            command.extend(["-p", "127.0.0.1::8080"])
        for name, value in environment.items():
            command.extend(["-e", name + "=" + value])
        identifier = docker(*command, options.image, *arguments)
        try:
            docker("start", identifier)
            yield identifier
        finally:
            docker("rm", "-f", identifier)

    def request(url, authorized=False, method="GET", path="/api/v1/platforms", body=None, cookie=False):
        headers = {}
        if authorized:
            headers["Authorization"] = "Bearer " + secret
        if cookie:
            headers["Cookie"] = "synthetic=ignored"
        if body is not None:
            headers["Content-Type"] = "application/json"
        outgoing = urllib.request.Request(
            url + path, data=None if body is None else body.encode(), headers=headers, method=method)
        try:
            response = http.open(outgoing, timeout=2)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, json.loads(response.read())

    def ready(identifier):
        port = docker("port", identifier, "8080/tcp").splitlines()[0].rsplit(":", 1)[1]
        url = "http://127.0.0.1:" + port
        for _ in range(100):
            if docker("inspect", "--format", "{{.State.Running}}", identifier) != "true":
                raise AssertionError("Valid switch configuration exited before listening")
            try:
                if request(url)[0] == 401:
                    return url
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(0.1)
        raise TimeoutError("Container did not begin listening")

    image_id, operating_system, architecture = docker(
        "image", "inspect", "--format", "{{.Id}}|{{.Os}}|{{.Architecture}}", options.image).split("|")
    image_environment = dict(item.split("=", 1) for item in json.loads(docker(
        "image", "inspect", "--format", "{{json .Config.Env}}", options.image)))
    check("linux-image", operating_system == "linux")
    check("switch-default-does-not-mask-other-sources", "VALIDATE_PUBLIC_IP" not in image_environment)

    modes = [
        ("default", {}, (), True),
        ("alias-true", {"VALIDATE_PUBLIC_IP": "true"}, (), True),
        ("alias-false", {"VALIDATE_PUBLIC_IP": "false"}, (), False),
        ("alias-uppercase", {"VALIDATE_PUBLIC_IP": "FALSE"}, (), False),
        ("native-false", {native: "false"}, (), False),
        ("alias-over-native", {"VALIDATE_PUBLIC_IP": "false", native: "true"}, (), False),
        ("command-enables", {"VALIDATE_PUBLIC_IP": "false"}, ("--" + key, "true"), True),
        ("command-disables", {"VALIDATE_PUBLIC_IP": "true"}, ("--" + key, "false"), False),
        ("command-over-invalid-alias", {"VALIDATE_PUBLIC_IP": invalid}, ("--" + key, "true"), True),
    ]
    for name, environment, arguments, enabled in modes:
        print("Checking " + name, flush=True)
        with container(environment, arguments, publish=True) as identifier:
            url = ready(identifier)
            status, body = request(url)
            check(name + "-authentication", status == 401 and body["error"]["code"] == "UNAUTHORIZED")
            status, body = request(url, authorized=True)
            check(name + "-discovery", status == 200
                  and {item["id"] for item in body["platforms"]} == {"douyin", "bilibili"})
            for platform in ("douyin", "bilibili"):
                status, body = request(url, authorized=True, method="POST",
                                       path=f"/api/v1/platforms/{platform}/resolve", body='{"text":"no URL"}')
                check(name + "-" + platform + "-input-boundary",
                      status == 400 and body["error"]["code"] == "INVALID_INPUT")
            status, body = request(url, authorized=True, cookie=True)
            check(name + "-cookie-rejected", status == 400 and body["error"]["code"] == "INVALID_INPUT")
            logs = docker("logs", identifier)
            check(name + "-startup-mode", (warning in logs) == (not enabled))
            check(name + "-safe-logs", secret not in logs and invalid not in logs)
            check(name + "-non-root-read-only", docker("exec", identifier, "id", "-u") != "0"
                  and docker("inspect", "--format", "{{.HostConfig.ReadonlyRootfs}}", identifier) == "true"
                  and docker("diff", identifier) == "")

    invalid_modes = [("alias-" + name, {"VALIDATE_PUBLIC_IP": value, native: "true"}, ())
                     for name, value in (("empty", ""), ("blank", " "), ("zero", "0"),
                                         ("one", "1"), ("yes", "yes"), ("sentinel", invalid))]
    invalid_modes.extend([
        ("native-invalid", {native: invalid}, ()),
        ("command-invalid", {"VALIDATE_PUBLIC_IP": "true"}, ("--" + key, invalid)),
    ])
    for name, environment, arguments in invalid_modes:
        print("Checking " + name, flush=True)
        with container(environment, arguments) as identifier:
            exit_code = int(docker("wait", identifier, timeout=15))
            logs = docker("logs", identifier)
            check(name + "-safe-startup-failure", exit_code == 1 and "Application started" not in logs
                  and "VALIDATE_PUBLIC_IP" in logs and warning not in logs
                  and secret not in logs and invalid not in logs)

    report = {
        "schemaVersion": 1,
        "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "image": options.image,
        "imageId": image_id,
        "architecture": architecture,
        "livePlatformRequests": False,
        "scope": "Production image switch configuration, startup warnings, safe failure, authentication, input and Cookie boundaries; no upstream connections.",
        "checks": checks,
    }
    options.output.write_text(json.dumps(report, indent=2) + "\n")
    print(f"Passed {len(checks)} checks; wrote {options.output}")


if __name__ == "__main__":
    main()
