"""Smoke test for the packaged tool: talks MCP over stdio to the installed `eflens` command.

Usage: python tests/smoke/smoke.py <command> [args...]
  e.g. python tests/smoke/smoke.py ./tools/eflens
       python tests/smoke/smoke.py dnx EfLens --add-source artifacts --yes

Configuration goes through environment variables, as an MCP client would pass it. It checks that every tool is
advertised and that ef_model can load samples/Shop, which proves the probe assembly ships inside the package.
"""

import json
import os
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import threading

EXPECTED_TOOLS = {"describe_table", "ef_drift", "ef_model", "explain", "list_relations", "list_tables", "query"}
ROOT = pathlib.Path(__file__).resolve().parents[2]


def main() -> int:
    command = sys.argv[1:]
    if not command:
        print(__doc__)
        return 2

    shop = next((ROOT / "samples" / "Shop" / "bin").glob("*/net10.0/Shop.dll"), None)
    if shop is None:
        print("Build samples/Shop first.")
        return 2

    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
        db = pathlib.Path(tmp) / "smoke.db"
        connection = sqlite3.connect(db)
        connection.execute("CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)")
        connection.commit()
        connection.close()

        env = dict(os.environ,
                   EfLens__Provider="sqlite",
                   EfLens__ConnectionString=f"Data Source={db}",
                   EfLens__EfAssembly=str(shop))
        server = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  env=env, text=True, encoding="utf-8")
        threading.Thread(target=lambda: server.stderr.read(), daemon=True).start()  # logs go to stderr
        timer = threading.Timer(120, server.kill)
        timer.start()

        def send(message: dict) -> None:
            server.stdin.write(json.dumps(message) + "\n")
            server.stdin.flush()

        def receive(request_id: int) -> dict:
            for line in server.stdout:
                message = json.loads(line)
                if message.get("id") == request_id:
                    if "error" in message:
                        raise RuntimeError(f"request {request_id} failed: {message['error']}")
                    return message["result"]
            raise RuntimeError(f"the server exited before answering request {request_id}")

        try:
            send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": "2025-06-18", "capabilities": {},
                "clientInfo": {"name": "eflens-smoke", "version": "1.0"}}})
            info = receive(1)["serverInfo"]
            send({"jsonrpc": "2.0", "method": "notifications/initialized"})

            send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
            tools = {tool["name"] for tool in receive(2)["tools"]}
            assert tools == EXPECTED_TOOLS, f"tools: {sorted(tools)}"

            send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "list_tables", "arguments": {}}})
            tables = receive(3)
            assert not tables.get("isError"), tables
            assert "Customers" in tables["content"][0]["text"], tables

            send({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "ef_model", "arguments": {}}})
            model = receive(4)
            assert not model.get("isError"), model
            assert "Shop.ShopContext" in model["content"][0]["text"], model
        finally:
            timer.cancel()
            server.stdin.close()
            server.kill()
            server.wait()

    print(f"Smoke test passed: {info['name']} {info['version']}, {len(tools)} tools, ef_model loaded Shop.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
