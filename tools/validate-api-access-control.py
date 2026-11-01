#!/usr/bin/env python3
"""Verify a built API image using synthetic credentials; remove only containers created here."""

import argparse
import base64
import contextlib
import datetime
import hashlib
import json
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", default="media-lens:local")
    parser.add_argument("--output", type=Path, default=Path("docs/validation/api-access-control.json"))
    options = parser.parse_args()
    checks = []
    http = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def docker(*arguments, timeout=30):
        result = subprocess.run(["docker", *arguments], capture_output=True, text=True, timeout=timeout)
        if result.returncode:
            raise RuntimeError(f"Docker {arguments[0]} failed: {result.stderr.strip()}")
        return (result.stdout + result.stderr if arguments[0] == "logs" else result.stdout).strip()

    def check(name, condition, **details):
        if not condition:
            raise AssertionError(name)
        checks.append({"name": name, "passed": True, **details})

    def no_secret(name, output, values):
        for value in values:
            if not value:
                continue
            digest = hashlib.sha256(value.encode()).digest()
            for representation in (value, digest.hex(), digest.hex().upper(), base64.b64encode(digest).decode()):
                if representation in output:
                    raise AssertionError(name + ": credential material appeared in output")

    @contextlib.contextmanager
    def container(environment, ports=(), arguments=()):
        name = "medialens-access-check-" + uuid.uuid4().hex
        command = ["create", "--name", name, "--read-only", "--tmpfs", "/tmp:rw,nosuid,size=32m"]
        for port in ports:
            command.extend(["-p", f"127.0.0.1::{port}"])
        for key, value in environment.items():
            command.extend(["-e", key + "=" + value])
        identifier = docker(*command, options.image, *arguments)
        try:
            docker("start", identifier)
            yield identifier
        finally:
            docker("rm", "-f", identifier)

    def endpoint(identifier, port):
        published = docker("port", identifier, f"{port}/tcp").splitlines()[0]
        return "http://127.0.0.1:" + published.rsplit(":", 1)[1]

    def request(url, token=None, method="GET", path="/api/v1/platforms", body=None, headers=None):
        fields = dict(headers or {})
        if token is not None:
            fields["Authorization"] = "Bearer " + token
        if body is not None:
            fields["Content-Type"] = "application/json"
        incoming = urllib.request.Request(
            url + path, data=None if body is None else body.encode(), headers=fields, method=method
        )
        try:
            response = http.open(incoming, timeout=2)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.headers, json.loads(response.read())

    def ready(identifier, port):
        url = endpoint(identifier, port)
        for _ in range(100):
            if docker("inspect", "--format", "{{.State.Running}}", identifier) != "true":
                raise AssertionError("Valid synthetic configuration exited before listening")
            try:
                if request(url)[0] == 401:
                    return url
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(0.1)
        raise TimeoutError("Container did not begin listening")

    def unauthorized(name, response):
        status, headers, body = response
        error = body.get("error", {})
        check(name, status == 401 and headers.get("WWW-Authenticate") == "Bearer"
              and set(body) == {"requestId", "error"} and bool(body["requestId"])
              and set(error) == {"code", "message", "retryable", "retryAfterSeconds"}
              and error["code"] == "UNAUTHORIZED" and error["retryable"] is False
              and error["retryAfterSeconds"] is None)

    image_id, operating_system, architecture = docker(
        "image", "inspect", "--format", "{{.Id}}|{{.Os}}|{{.Architecture}}", options.image
    ).split("|")
    check("linux-image", operating_system == "linux", architecture=architecture)
    image_environment = dict(item.split("=", 1) for item in json.loads(docker(
        "image", "inspect", "--format", "{{json .Config.Env}}", options.image
    )))
    check("http-port-image-default", image_environment.get("HTTP_PORT") == "8080")
    valid = "A" * 64
    invalid = [
        ("missing-secret", {}),
        ("empty-secret", {"SECRET_TOKEN": ""}),
        ("blank-secret", {"SECRET_TOKEN": " " * 32}),
        ("secret-31", {"SECRET_TOKEN": "A" * 31}),
        ("secret-257", {"SECRET_TOKEN": "A" * 257}),
        ("non-ascii-secret", {"SECRET_TOKEN": "A" * 31 + "é"}),
        ("invalid-alias-no-fallback", {"SECRET_TOKEN": "short", "MediaLens__Auth__SecretToken": valid}),
        ("port-zero", {"SECRET_TOKEN": valid, "HTTP_PORT": "0"}),
        ("port-overflow", {"SECRET_TOKEN": valid, "HTTP_PORT": "65536"}),
        ("port-list-alias", {"SECRET_TOKEN": valid, "HTTP_PORT": "9000;9001"}),
    ]
    for name, environment in invalid:
        print("Checking " + name, flush=True)
        with container(environment) as identifier:
            try:
                exit_code = int(docker("wait", identifier, timeout=15))
            except subprocess.TimeoutExpired as error:
                output = docker("logs", identifier)
                no_secret(name, output, (value for key, value in environment.items()
                                        if key in {"SECRET_TOKEN", "MediaLens__Auth__SecretToken"}))
                raise AssertionError(name + " did not exit: " + output) from error
            output = docker("logs", identifier)
            no_secret(name, output, (value for key, value in environment.items()
                                    if key in {"SECRET_TOKEN", "MediaLens__Auth__SecretToken"}))
            check(name, exit_code != 0 and "Application started" not in output, exitCode=exit_code)

    for length in (32, 256):
        token = "A" * length
        with container({"SECRET_TOKEN": token}, ports=(8080,)) as identifier:
            url = ready(identifier, 8080)
            unauthorized(f"secret-{length}-missing-header", request(url))
            status, _, body = request(url, token)
            check(f"secret-{length}-discovery", status == 200
                  and {item["id"] for item in body["platforms"]} == {"douyin", "bilibili"})
            unauthorized(f"secret-{length}-mismatch", request(url, "B" * length))
            for bad_length in (31, 257):
                for method, path in (("GET", "/api/v1/platforms"), ("POST", "/api/v1/resolve"),
                                     ("POST", "/api/v1/platforms/douyin/resolve")):
                    unauthorized(f"secret-{length}-request-{bad_length}-{path}", request(
                        url, "A" * bad_length, method=method, path=path,
                        body=None if method == "GET" else "{invalid"
                    ))
            for path in ("/api/v1/resolve", "/api/v1/platforms/douyin/resolve", "/api/v1/platforms/bilibili/resolve"):
                status, _, body = request(url, token, method="POST", path=path, body='{"text":"no URL"}')
                check(f"secret-{length}-authorized-input-validation-{path}",
                      status == 400 and body["error"]["code"] == "INVALID_INPUT")
            status, _, _ = request(url, token, headers={"Cookie": "synthetic=ignored"})
            check(f"secret-{length}-cookie-rejected", status == 400)
            check(f"secret-{length}-read-only", docker("inspect", "--format", "{{.HostConfig.ReadonlyRootfs}}", identifier) == "true")
            check(f"secret-{length}-non-root", docker("exec", identifier, "id", "-u") != "0")
            check(f"secret-{length}-no-root-filesystem-writes", docker("diff", identifier) == "")
            no_secret("container-logs", docker("logs", identifier), (token, "A" * 31, "A" * 257, "B" * length))

    modes = [
        ("alias-port", {"SECRET_TOKEN": valid, "HTTP_PORT": "9000"}, (9000,), (), valid),
        ("native-secret", {"MediaLens__Auth__SecretToken": valid}, (8080,), (), valid),
        ("command-secret", {"SECRET_TOKEN": "B" * 64}, (8080,), ("--MediaLens:Auth:SecretToken", valid), valid),
        ("command-port", {"SECRET_TOKEN": valid, "HTTP_PORT": "not-selected"}, (9001,), ("--HTTP_PORTS", "9001"), valid),
        ("command-url", {"SECRET_TOKEN": valid, "HTTP_PORT": "not-selected"}, (9002,), ("--urls", "http://+:9002"), valid),
        ("native-url", {"SECRET_TOKEN": valid, "HTTP_PORT": "not-selected", "ASPNETCORE_URLS": "http://+:9003"}, (9003,), (), valid),
    ]
    for name, environment, ports, arguments, token in modes:
        with container(environment, ports, arguments) as identifier:
            for port in ports:
                url = ready(identifier, port)
                check(name + f"-{port}", request(url, token)[0] == 200)
            if name == "command-secret":
                unauthorized("command-secret-rejects-alias", request(url, "B" * 64))
            no_secret(name, docker("logs", identifier), (token, "B" * 64))

    report = {
        "schemaVersion": 1,
        "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "image": options.image,
        "imageId": image_id,
        "architecture": architecture,
        "livePlatformRequests": False,
        "scope": "Production image startup, authentication, both platform routes' input errors, port publishing, read-only non-root runtime; synthetic inputs only.",
        "passed": len(checks),
        "checks": checks,
    }
    options.output.parent.mkdir(parents=True, exist_ok=True)
    options.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(f"Passed {len(checks)} Docker checks; sanitized report: {options.output}")


if __name__ == "__main__":
    main()
