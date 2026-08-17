#!/usr/bin/env python3
"""
llama_tool_eval.py

A dependency-free tool/function-calling regression harness for llama.cpp's
OpenAI-compatible /v1/chat/completions API.

What it measures:
  - Tool selection
  - Tool restraint (knowing when NOT to call a tool)
  - Argument/schema correctness
  - Tool-result grounding
  - Multi-step tool use / follow-up behavior
  - Error recovery
  - Bash vs PowerShell vs cmd.exe awareness
  - Paths, environment variables, quoting, pipes, and shell syntax

It does NOT execute model-generated shell commands. Shell tests are static and safe.

Examples:
  python llama_tool_eval.py --url http://127.0.0.1:8080/v1
  python llama_tool_eval.py --url http://127.0.0.1:8080/v1 --runs 5
  python llama_tool_eval.py --model my-model --out results/qwen3

Outputs:
  <out>.json   full raw results
  <out>.csv    one row per test run

Recommended llama.cpp server configuration for tool calling:
  llama-server -m model.gguf --jinja --host 127.0.0.1 --port 8080
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple


# ----------------------------- HTTP ----------------------------------------- #

def http_json(method: str, url: str, payload: Optional[dict] = None,
              timeout: int = 120) -> dict:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        method=method,
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read().decode("utf-8", errors="replace")
            return json.loads(raw)
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", errors="replace")
        try:
            detail = json.loads(raw)
        except Exception:
            detail = raw
        raise RuntimeError(f"HTTP {e.code} from {url}: {detail}") from e
    except urllib.error.URLError as e:
        raise RuntimeError(f"Could not connect to {url}: {e}") from e


def normalize_base_url(url: str) -> str:
    return url.rstrip("/")


def list_models(base_url: str, timeout: int) -> List[str]:
    data = http_json("GET", f"{base_url}/models", timeout=timeout)
    models = data.get("data") or []
    if not models:
        raise RuntimeError("/models returned no models")
    ids = []
    for i, item in enumerate(models):
        model_id = item.get("id")
        if model_id:
            ids.append(str(model_id))
    if not ids:
        raise RuntimeError("/models response did not contain any model ids")
    return ids


# ----------------------------- Tool schemas -------------------------------- #

def fn(name: str, description: str, properties: dict,
       required: Optional[List[str]] = None) -> dict:
    schema = {
        "type": "object",
        "properties": properties,
        "additionalProperties": False,
    }
    if required:
        schema["required"] = required
    return {
        "type": "function",
        "function": {
            "name": name,
            "description": description,
            "parameters": schema,
        },
    }


CALCULATOR = fn(
    "calculator",
    "Evaluate a basic arithmetic expression exactly.",
    {"expression": {"type": "string", "description": "Arithmetic expression"}},
    ["expression"],
)

WEATHER = fn(
    "get_weather",
    "Get current weather for a city.",
    {
        "city": {"type": "string"},
        "unit": {"type": "string", "enum": ["celsius", "fahrenheit"]},
    },
    ["city"],
)

FILE_READ = fn(
    "read_file",
    "Read a text file from an exact path.",
    {"path": {"type": "string"}},
    ["path"],
)

FILE_LIST = fn(
    "list_directory",
    "List files and directories at a path.",
    {
        "path": {"type": "string"},
        "include_hidden": {"type": "boolean"},
    },
    ["path"],
)

SEARCH = fn(
    "search_documents",
    "Search indexed documents for a query.",
    {
        "query": {"type": "string"},
        "limit": {"type": "integer", "minimum": 1, "maximum": 20},
    },
    ["query"],
)

SHELL = fn(
    "execute_command",
    "Execute a command in the explicitly selected shell. Use the OS/shell requested by the user.",
    {
        "shell": {"type": "string", "enum": ["bash", "powershell", "cmd"]},
        "command": {"type": "string"},
    },
    ["shell", "command"],
)

LOOKUP_USER = fn(
    "lookup_user",
    "Look up a user account by username.",
    {"username": {"type": "string"}},
    ["username"],
)

SEND_MESSAGE = fn(
    "send_message",
    "Send a message to a user ID.",
    {
        "user_id": {"type": "string"},
        "message": {"type": "string"},
    },
    ["user_id", "message"],
)

ALL_BASIC_TOOLS = [CALCULATOR, WEATHER, FILE_READ, FILE_LIST, SEARCH]


# ----------------------------- Parsing -------------------------------------- #

def get_choice_message(resp: dict) -> dict:
    choices = resp.get("choices")
    if not isinstance(choices, list) or not choices:
        raise ValueError("Response has no choices[0]")
    msg = choices[0].get("message")
    if not isinstance(msg, dict):
        raise ValueError("Response has no choices[0].message")
    return msg


def normalize_arguments(value: Any) -> Tuple[Optional[dict], Optional[str]]:
    """
    llama.cpp/OpenAI-compatible servers may expose arguments as JSON text or,
    in some versions/configurations, as an object. Accept both.
    """
    if isinstance(value, dict):
        return value, None
    if isinstance(value, str):
        try:
            obj = json.loads(value)
            if isinstance(obj, dict):
                return obj, None
            return None, "arguments_json_not_object"
        except json.JSONDecodeError:
            return None, "malformed_arguments_json"
    return None, "arguments_wrong_type"


def extract_tool_calls(resp: dict) -> Tuple[List[dict], Optional[str]]:
    try:
        msg = get_choice_message(resp)
    except Exception as e:
        return [], f"invalid_response:{e}"

    calls = msg.get("tool_calls")
    if calls is None:
        return [], None
    if not isinstance(calls, list):
        return [], "tool_calls_not_list"

    out = []
    for i, call in enumerate(calls):
        if not isinstance(call, dict):
            return [], f"tool_call_{i}_not_object"
        f = call.get("function")
        if not isinstance(f, dict):
            return [], f"tool_call_{i}_missing_function"
        name = f.get("name")
        args, arg_error = normalize_arguments(f.get("arguments"))
        out.append({
            "id": call.get("id"),
            "type": call.get("type"),
            "name": name,
            "arguments": args,
            "argument_error": arg_error,
            "raw": call,
        })
    return out, None


def assistant_text(resp: dict) -> str:
    try:
        content = get_choice_message(resp).get("content")
    except Exception:
        return ""
    if content is None:
        return ""
    if isinstance(content, str):
        return content
    return json.dumps(content, ensure_ascii=False)


# ----------------------------- Evaluation helpers --------------------------- #

@dataclass
class EvalResult:
    passed: bool
    failure_class: str = ""
    detail: str = ""
    score: float = 1.0


Validator = Callable[[dict, List[dict]], EvalResult]


def pass_result(detail: str = "") -> EvalResult:
    return EvalResult(True, "", detail, 1.0)


def fail(kind: str, detail: str = "", score: float = 0.0) -> EvalResult:
    return EvalResult(False, kind, detail, score)


def expect_single_tool(name: str, expected_args: Optional[dict] = None,
                       arg_predicate: Optional[Callable[[dict], Tuple[bool, str]]] = None) -> Validator:
    def validate(resp: dict, calls: List[dict]) -> EvalResult:
        if not calls:
            text = assistant_text(resp)
            return fail("missing_tool_call", f"Expected {name}; assistant text={text[:200]!r}")
        if len(calls) != 1:
            return fail("wrong_tool_count", f"Expected 1 call, got {len(calls)}")
        c = calls[0]
        if c.get("argument_error"):
            return fail(c["argument_error"], f"Raw call: {c.get('raw')}")
        if c.get("name") != name:
            return fail("wrong_tool", f"Expected {name}, got {c.get('name')}")
        args = c.get("arguments") or {}
        if expected_args is not None:
            for k, v in expected_args.items():
                if args.get(k) != v:
                    return fail("wrong_argument", f"Expected {k}={v!r}, got {args.get(k)!r}; args={args}")
        if arg_predicate:
            ok, detail = arg_predicate(args)
            if not ok:
                return fail("wrong_argument", detail)
        return pass_result()
    return validate


def expect_no_tool(required_text: Optional[str] = None) -> Validator:
    def validate(resp: dict, calls: List[dict]) -> EvalResult:
        if calls:
            return fail("unnecessary_tool_call",
                        f"Expected no tool; got {[c.get('name') for c in calls]}")
        if required_text:
            text = assistant_text(resp).lower()
            if required_text.lower() not in text:
                return fail("wrong_answer", f"Expected {required_text!r} in {text[:250]!r}")
        return pass_result()
    return validate


def shell_validator(expected_shell: str,
                    command_predicate: Callable[[str], Tuple[bool, str]]) -> Validator:
    def validate(resp: dict, calls: List[dict]) -> EvalResult:
        base = expect_single_tool("execute_command")(resp, calls)
        if not base.passed:
            return base
        args = calls[0]["arguments"] or {}
        shell = str(args.get("shell", "")).lower()
        cmd = str(args.get("command", ""))
        if shell != expected_shell:
            return fail("wrong_shell", f"Expected {expected_shell}, got {shell!r}; command={cmd!r}")
        ok, detail = command_predicate(cmd)
        if not ok:
            return fail("wrong_command_for_shell", detail)
        return pass_result()
    return validate


def contains_all(*needles: str) -> Callable[[str], Tuple[bool, str]]:
    def pred(cmd: str) -> Tuple[bool, str]:
        lower = cmd.lower()
        missing = [n for n in needles if n.lower() not in lower]
        if missing:
            return False, f"Command {cmd!r} missing expected token(s): {missing}"
        return True, ""
    return pred


def contains_any(*needles: str) -> Callable[[str], Tuple[bool, str]]:
    def pred(cmd: str) -> Tuple[bool, str]:
        lower = cmd.lower()
        if not any(n.lower() in lower for n in needles):
            return False, f"Command {cmd!r} should contain one of {needles}"
        return True, ""
    return pred


def regex_pred(pattern: str, description: str) -> Callable[[str], Tuple[bool, str]]:
    rx = re.compile(pattern, re.I)
    def pred(cmd: str) -> Tuple[bool, str]:
        if not rx.search(cmd):
            return False, f"Command {cmd!r} does not satisfy: {description}"
        return True, ""
    return pred


def and_pred(*preds):
    def pred(cmd: str):
        for p in preds:
            ok, detail = p(cmd)
            if not ok:
                return ok, detail
        return True, ""
    return pred


# ----------------------------- Tests ---------------------------------------- #

@dataclass
class TestCase:
    id: str
    category: str
    prompt: str
    tools: List[dict]
    validator: Validator
    system: str = (
        "You are being evaluated for correct tool use. "
        "Use a tool only when appropriate. Follow the user's requested operating system "
        "and shell exactly. Do not claim a tool ran unless its result is provided."
    )
    tool_choice: Any = "auto"
    followup: Optional[str] = None
    kind: str = "single"


def city_pred(expected_city: str):
    def pred(args):
        city = str(args.get("city", "")).strip().lower()
        return (city == expected_city.lower(),
                f"Expected city={expected_city!r}, got {args.get('city')!r}")
    return pred


def calculator_pred(expected_numbers: Tuple[str, ...]):
    def pred(args):
        expr = str(args.get("expression", ""))
        ok = all(n in expr for n in expected_numbers)
        return ok, f"Expression {expr!r} should contain {expected_numbers}"
    return pred


def search_limit_pred(args):
    if "limit" not in args:
        return True, ""
    return isinstance(args["limit"], int), f"limit should be integer, got {type(args['limit']).__name__}"


TESTS: List[TestCase] = [
    TestCase(
        "tool_calc_01", "tool_selection",
        "Use the calculator tool to compute 174 * 93.",
        [CALCULATOR],
        expect_single_tool("calculator", arg_predicate=calculator_pred(("174", "93"))),
    ),
    TestCase(
        "tool_weather_01", "tool_selection",
        "What is the weather in Helsinki? Use the weather tool.",
        [WEATHER],
        expect_single_tool("get_weather", arg_predicate=city_pred("helsinki")),
    ),
    TestCase(
        "tool_file_01", "arguments",
        r"Read the file /tmp/alpha notes.txt using the available tool.",
        [FILE_READ],
        expect_single_tool("read_file", expected_args={"path": "/tmp/alpha notes.txt"}),
    ),
    TestCase(
        "tool_bool_01", "arguments",
        "List /var/tmp and include hidden files.",
        [FILE_LIST],
        expect_single_tool("list_directory",
                           expected_args={"path": "/var/tmp", "include_hidden": True}),
    ),
    TestCase(
        "tool_type_01", "arguments",
        "Search documents for 'kernel panic' and return at most 5 results.",
        [SEARCH],
        expect_single_tool("search_documents", arg_predicate=search_limit_pred),
    ),
    TestCase(
        "tool_disambiguate_01", "tool_selection",
        "Find files in /srv/reports. Do not search document contents.",
        [FILE_LIST, SEARCH],
        expect_single_tool("list_directory", expected_args={"path": "/srv/reports"}),
    ),
    TestCase(
        "restraint_01", "tool_restraint",
        "Answer without tools: What is the capital of France?",
        [CALCULATOR, WEATHER],
        expect_no_tool("paris"),
    ),
    TestCase(
        "restraint_02", "tool_restraint",
        "Say exactly: TOOL_TEST_OK. Do not use a tool.",
        ALL_BASIC_TOOLS,
        expect_no_tool("tool_test_ok"),
    ),
    TestCase(
        "restraint_03", "tool_restraint",
        "I want you to reboot the moon. If no available tool can do that, explain briefly rather than inventing one.",
        [CALCULATOR, FILE_READ],
        expect_no_tool(),
    ),

    # Bash
    TestCase(
        "bash_hidden_01", "shell_bash",
        "On Linux using bash, list all files including hidden files in /tmp.",
        [SHELL],
        shell_validator("bash", and_pred(contains_any("ls"), contains_any("-a", "--all"))),
    ),
    TestCase(
        "bash_env_01", "shell_bash",
        "On Linux using bash, print the current user's home directory using the HOME environment variable.",
        [SHELL],
        shell_validator("bash", contains_any("$home", "${home}")),
    ),
    TestCase(
        "bash_path_01", "shell_bash",
        r"On Linux using bash, print the contents of /tmp/my notes/report.txt.",
        [SHELL],
        shell_validator("bash",
            and_pred(contains_any("cat", "sed", "less", "head", "tail"),
                     contains_any("'/tmp/my notes/report.txt'", '"/tmp/my notes/report.txt"', r"/tmp/my\ notes/report.txt"))),
    ),
    TestCase(
        "bash_pipe_01", "shell_bash",
        "On Linux using bash, list processes and filter for processes containing python.",
        [SHELL],
        shell_validator("bash", and_pred(contains_any("ps"), contains_any("|"), contains_any("grep", "rg"))),
    ),

    # PowerShell
    TestCase(
        "ps_hidden_01", "shell_powershell",
        r"On Windows PowerShell, list all files including hidden files in C:\Temp.",
        [SHELL],
        shell_validator("powershell",
            and_pred(contains_any("get-childitem", "gci", "dir"),
                     contains_any("-force"))),
    ),
    TestCase(
        "ps_env_01", "shell_powershell",
        "On Windows PowerShell, print the USERPROFILE environment variable.",
        [SHELL],
        shell_validator("powershell", contains_any("$env:userprofile")),
    ),
    TestCase(
        "ps_path_01", "shell_powershell",
        r"On Windows PowerShell, print the contents of C:\Program Files\Acme\notes.txt.",
        [SHELL],
        shell_validator("powershell",
            and_pred(contains_any("get-content", "gc", "type"),
                     contains_any(r"c:\program files\acme\notes.txt"))),
    ),
    TestCase(
        "ps_filter_01", "shell_powershell",
        "On Windows PowerShell, list running processes whose process name contains python.",
        [SHELL],
        shell_validator("powershell",
            and_pred(contains_any("get-process", "gps"),
                     contains_any("where-object", "where", "?"))),
    ),

    # cmd.exe
    TestCase(
        "cmd_hidden_01", "shell_cmd",
        r"Using Windows cmd.exe specifically, list all files including hidden files in C:\Temp.",
        [SHELL],
        shell_validator("cmd", and_pred(contains_any("dir"), contains_any("/a"))),
    ),
    TestCase(
        "cmd_env_01", "shell_cmd",
        "Using Windows cmd.exe specifically, print the USERPROFILE environment variable.",
        [SHELL],
        shell_validator("cmd", contains_any("%userprofile%")),
    ),
    TestCase(
        "cmd_path_01", "shell_cmd",
        r'Using Windows cmd.exe specifically, print C:\Program Files\Acme\notes.txt.',
        [SHELL],
        shell_validator("cmd",
            and_pred(contains_any("type"),
                     contains_any(r'"c:\program files\acme\notes.txt"'))),
    ),
    TestCase(
        "cmd_filter_01", "shell_cmd",
        "Using Windows cmd.exe specifically, list running processes and filter lines containing python.",
        [SHELL],
        shell_validator("cmd",
            and_pred(contains_any("tasklist"),
                     contains_any("findstr", "find"))),
    ),

    # Explicit shell switching
    TestCase(
        "shell_switch_01", "shell_identification",
        "This machine is Windows, but the command will run inside Git Bash. Print HOME.",
        [SHELL],
        shell_validator("bash", contains_any("$home", "${home}")),
    ),
    TestCase(
        "shell_switch_02", "shell_identification",
        "This machine is Windows. Use PowerShell, not cmd.exe, to print PATH.",
        [SHELL],
        shell_validator("powershell", contains_any("$env:path")),
    ),
    TestCase(
        "shell_switch_03", "shell_identification",
        "This machine is Windows. Use cmd.exe, not PowerShell, to print PATH.",
        [SHELL],
        shell_validator("cmd", contains_any("%path%")),
    ),
]


# ----------------------------- Multi-turn tests ----------------------------- #

@dataclass
class MultiTurnSpec:
    id: str
    category: str
    initial_prompt: str
    tools: List[dict]
    first_tool: str
    fake_result: str
    final_required: Optional[str] = None
    expect_second_tool: Optional[str] = None
    fake_second_result: Optional[str] = None
    second_args_check: Optional[Callable[[dict], Tuple[bool, str]]] = None


MULTI_TESTS = [
    MultiTurnSpec(
        "followup_nonce_01", "tool_result_grounding",
        "Read /virtual/nonce.txt and tell me its contents.",
        [FILE_READ],
        "read_file",
        "NONCE-7F3A91",
        final_required="NONCE-7F3A91",
    ),
    MultiTurnSpec(
        "followup_error_01", "error_recovery",
        "Read /virtual/missing.txt. If it is missing, report the error accurately; do not invent contents.",
        [FILE_READ],
        "read_file",
        "ERROR: ENOENT: file does not exist",
        final_required="ENOENT",
    ),
    MultiTurnSpec(
        "multistep_user_01", "multi_step",
        "Look up user alice, then send her the message 'build passed'.",
        [LOOKUP_USER, SEND_MESSAGE],
        "lookup_user",
        '{"user_id":"user-4821","username":"alice"}',
        expect_second_tool="send_message",
        second_args_check=lambda a: (
            a.get("user_id") == "user-4821" and a.get("message") == "build passed",
            f"Expected user_id='user-4821', message='build passed'; got {a}",
        ),
    ),
]


# ----------------------------- Runner --------------------------------------- #

def make_request(model: str, messages: list, tools: list, temperature: float,
                 seed: Optional[int], max_tokens: int, tool_choice: Any) -> dict:
    payload = {
        "model": model,
        "messages": messages,
        "temperature": temperature,
        "max_tokens": max_tokens,
    }
    if tools:
        payload["tools"] = tools
        payload["tool_choice"] = tool_choice
    if seed is not None:
        payload["seed"] = seed
    return payload


def classify_transport_exception(exc: Exception) -> str:
    text = str(exc).lower()
    if "could not connect" in text:
        return "connection_error"
    if "http 500" in text:
        return "server_error_500"
    if "http 400" in text:
        return "server_error_400"
    return "transport_error"


def run_single(base_url: str, model: str, test: TestCase, temperature: float,
               seed: Optional[int], timeout: int, max_tokens: int) -> dict:
    messages = [
        {"role": "system", "content": test.system},
        {"role": "user", "content": test.prompt},
    ]
    payload = make_request(model, messages, test.tools, temperature, seed,
                           max_tokens, test.tool_choice)
    started = time.time()
    try:
        resp = http_json("POST", f"{base_url}/chat/completions", payload, timeout)
        elapsed = time.time() - started
        calls, parse_error = extract_tool_calls(resp)
        if parse_error:
            ev = fail("response_parse_error", parse_error)
        else:
            ev = test.validator(resp, calls)
        return {
            "test_id": test.id,
            "category": test.category,
            "prompt": test.prompt,
            "passed": ev.passed,
            "score": ev.score,
            "failure_class": ev.failure_class,
            "detail": ev.detail,
            "elapsed_s": round(elapsed, 4),
            "tool_calls": calls,
            "assistant_text": assistant_text(resp),
            "raw_response": resp,
            "request": payload,
        }
    except Exception as e:
        return {
            "test_id": test.id,
            "category": test.category,
            "prompt": test.prompt,
            "passed": False,
            "score": 0.0,
            "failure_class": classify_transport_exception(e),
            "detail": str(e),
            "elapsed_s": round(time.time() - started, 4),
            "tool_calls": [],
            "assistant_text": "",
            "raw_response": None,
            "request": payload,
        }


def run_multi(base_url: str, model: str, spec: MultiTurnSpec, temperature: float,
              seed: Optional[int], timeout: int, max_tokens: int) -> dict:
    system = (
        "You are being evaluated for correct tool use. Use tools when required. "
        "Never invent a tool result. After receiving a tool result, use it accurately."
    )
    messages = [
        {"role": "system", "content": system},
        {"role": "user", "content": spec.initial_prompt},
    ]
    payload1 = make_request(model, messages, spec.tools, temperature, seed,
                            max_tokens, "auto")
    started = time.time()
    raw_responses = []
    try:
        resp1 = http_json("POST", f"{base_url}/chat/completions", payload1, timeout)
        raw_responses.append(resp1)
        calls1, parse_error = extract_tool_calls(resp1)
        if parse_error:
            raise ValueError(parse_error)
        if not calls1:
            return _multi_fail(spec, payload1, raw_responses, started,
                               "missing_tool_call",
                               f"Expected initial tool {spec.first_tool}")
        c1 = calls1[0]
        if c1.get("argument_error"):
            return _multi_fail(spec, payload1, raw_responses, started,
                               c1["argument_error"], str(c1.get("raw")))
        if c1.get("name") != spec.first_tool:
            return _multi_fail(spec, payload1, raw_responses, started,
                               "wrong_tool",
                               f"Expected first {spec.first_tool}, got {c1.get('name')}")

        # Preserve assistant message as returned, including its tool_calls.
        assistant_msg = get_choice_message(resp1).copy()
        messages.append(assistant_msg)

        # OpenAI-compatible tool result message.
        tool_id = c1.get("id")
        tool_msg = {
            "role": "tool",
            "content": spec.fake_result,
        }
        if tool_id is not None:
            tool_msg["tool_call_id"] = tool_id
        messages.append(tool_msg)

        payload2 = make_request(model, messages, spec.tools, temperature, seed,
                                max_tokens, "auto")
        resp2 = http_json("POST", f"{base_url}/chat/completions", payload2, timeout)
        raw_responses.append(resp2)
        calls2, parse_error2 = extract_tool_calls(resp2)
        if parse_error2:
            return _multi_fail(spec, payload2, raw_responses, started,
                               "response_parse_error", parse_error2)

        if spec.expect_second_tool:
            if not calls2:
                return _multi_fail(spec, payload2, raw_responses, started,
                                   "missing_second_tool_call",
                                   f"Expected {spec.expect_second_tool}")
            c2 = calls2[0]
            if c2.get("argument_error"):
                return _multi_fail(spec, payload2, raw_responses, started,
                                   c2["argument_error"], str(c2.get("raw")))
            if c2.get("name") != spec.expect_second_tool:
                return _multi_fail(spec, payload2, raw_responses, started,
                                   "wrong_second_tool",
                                   f"Expected {spec.expect_second_tool}, got {c2.get('name')}")
            if spec.second_args_check:
                ok, detail = spec.second_args_check(c2.get("arguments") or {})
                if not ok:
                    return _multi_fail(spec, payload2, raw_responses, started,
                                       "wrong_second_tool_arguments", detail)
            return {
                "test_id": spec.id,
                "category": spec.category,
                "prompt": spec.initial_prompt,
                "passed": True,
                "score": 1.0,
                "failure_class": "",
                "detail": "",
                "elapsed_s": round(time.time() - started, 4),
                "tool_calls": [c1] + calls2,
                "assistant_text": assistant_text(resp2),
                "raw_response": raw_responses,
                "request": [payload1, payload2],
            }

        if calls2:
            return _multi_fail(spec, payload2, raw_responses, started,
                               "unexpected_second_tool_call",
                               f"Expected final answer; got {[c.get('name') for c in calls2]}")

        text = assistant_text(resp2)
        if spec.final_required and spec.final_required.lower() not in text.lower():
            kind = ("ignored_tool_result" if "nonce" in spec.id
                    else "incorrect_error_recovery")
            return _multi_fail(spec, payload2, raw_responses, started, kind,
                               f"Final text did not contain {spec.final_required!r}: {text[:300]!r}")

        return {
            "test_id": spec.id,
            "category": spec.category,
            "prompt": spec.initial_prompt,
            "passed": True,
            "score": 1.0,
            "failure_class": "",
            "detail": "",
            "elapsed_s": round(time.time() - started, 4),
            "tool_calls": [c1],
            "assistant_text": text,
            "raw_response": raw_responses,
            "request": [payload1, payload2],
        }

    except Exception as e:
        return _multi_fail(spec, payload1, raw_responses, started,
                           classify_transport_exception(e), str(e))


def _multi_fail(spec, request, responses, started, kind, detail):
    return {
        "test_id": spec.id,
        "category": spec.category,
        "prompt": spec.initial_prompt,
        "passed": False,
        "score": 0.0,
        "failure_class": kind,
        "detail": detail,
        "elapsed_s": round(time.time() - started, 4),
        "tool_calls": [],
        "assistant_text": "",
        "raw_response": responses,
        "request": request,
    }


# ----------------------------- Reporting ------------------------------------ #

def pct(n: int, d: int) -> str:
    return "n/a" if d == 0 else f"{100.0*n/d:5.1f}%"


def print_summary(results: List[dict], model: str, base_url: str, runs: int) -> None:
    width = 72
    print("\n" + "=" * width)
    print("LLAMA.CPP TOOL EVALUATION")
    print("=" * width)
    print(f"Model:       {model}")
    print(f"Endpoint:    {base_url}")
    print(f"Runs/test:   {runs}")
    print(f"Total runs:  {len(results)}")

    cats: Dict[str, List[dict]] = {}
    for r in results:
        cats.setdefault(r["category"], []).append(r)

    print("\nCATEGORY SCORES")
    print("-" * width)
    for cat in sorted(cats):
        rs = cats[cat]
        passed = sum(1 for x in rs if x["passed"])
        print(f"{cat:32s} {passed:4d}/{len(rs):<4d} {pct(passed, len(rs)):>8s}")

    total_passed = sum(1 for r in results if r["passed"])
    print("-" * width)
    print(f"{'OVERALL':32s} {total_passed:4d}/{len(results):<4d} {pct(total_passed, len(results)):>8s}")

    failures: Dict[str, int] = {}
    for r in results:
        if not r["passed"]:
            failures[r["failure_class"] or "unknown"] = failures.get(r["failure_class"] or "unknown", 0) + 1

    print("\nFAILURE BREAKDOWN")
    print("-" * width)
    if not failures:
        print("No failures.")
    else:
        for kind, count in sorted(failures.items(), key=lambda kv: (-kv[1], kv[0])):
            print(f"{kind:40s} {count:4d}")

    print("\nFAILED TESTS")
    print("-" * width)
    failed = [r for r in results if not r["passed"]]
    if not failed:
        print("None.")
    else:
        for r in failed[:30]:
            print(f"{r['test_id']:24s} {r['failure_class']:28s} {r['detail'][:100]}")
        if len(failed) > 30:
            print(f"... and {len(failed)-30} more (see JSON output).")
    print("=" * width)


def write_json(path: Path, metadata: dict, results: List[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    obj = {
        "metadata": metadata,
        "summary": {
            "total": len(results),
            "passed": sum(1 for r in results if r["passed"]),
            "failed": sum(1 for r in results if not r["passed"]),
        },
        "results": results,
    }
    path.write_text(json.dumps(obj, indent=2, ensure_ascii=False), encoding="utf-8")


def write_csv(path: Path, results: List[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fields = [
        "test_id", "run", "category", "passed", "score", "failure_class",
        "detail", "elapsed_s", "assistant_text", "tool_calls"
    ]
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        w.writeheader()
        for r in results:
            w.writerow({
                "test_id": r["test_id"],
                "run": r.get("run", 1),
                "category": r["category"],
                "passed": r["passed"],
                "score": r["score"],
                "failure_class": r["failure_class"],
                "detail": r["detail"],
                "elapsed_s": r["elapsed_s"],
                "assistant_text": r["assistant_text"],
                "tool_calls": json.dumps(r["tool_calls"], ensure_ascii=False),
            })


def select_tests(suite: str) -> Tuple[List[TestCase], List[MultiTurnSpec]]:
    if suite == "all":
        return TESTS, MULTI_TESTS
    if suite == "tools":
        singles = [t for t in TESTS if not t.category.startswith("shell_")]
        return singles, MULTI_TESTS
    if suite == "shell":
        singles = [t for t in TESTS if t.category.startswith("shell_")]
        return singles, []
    raise ValueError(suite)


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Evaluate llama.cpp OpenAI-compatible tool calling.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    ap.add_argument("--url", default="http://127.0.0.1:8080/v1",
                    help="OpenAI-compatible base URL")
    ap.add_argument("--model", action="append", default=None,
                    help="Model ID to test. May be repeated. If omitted, test ALL models from /v1/models.")
    ap.add_argument("--list-models", action="store_true",
                    help="List model IDs from /v1/models and exit.")
    ap.add_argument("--runs", type=int, default=1,
                    help="Number of repetitions for each test")
    ap.add_argument("--temperature", type=float, default=0.0)
    ap.add_argument("--seed", type=int, default=12345,
                    help="Seed sent to server. Use --seed -1 to omit.")
    ap.add_argument("--timeout", type=int, default=120)
    ap.add_argument("--max-tokens", type=int, default=384)
    ap.add_argument("--suite", choices=["all", "tools", "shell"], default="all")
    ap.add_argument("--out", default="llama_tool_eval_results",
                    help="Output basename (without .json/.csv)")
    ap.add_argument("--stop-on-server-error", action="store_true")
    args = ap.parse_args()

    if args.runs < 1:
        ap.error("--runs must be >= 1")

    base_url = normalize_base_url(args.url)
    seed = None if args.seed < 0 else args.seed

    try:
        available_models = list_models(base_url, args.timeout)
    except Exception as e:
        if args.model:
            available_models = args.model
        else:
            print(f"ERROR: Unable to determine models: {e}", file=sys.stderr)
            print("If /v1/models is unavailable, pass one or more --model values explicitly.", file=sys.stderr)
            return 2

    if args.list_models:
        for m in available_models:
            print(m)
        return 0

    models = args.model or available_models

    singles, multis = select_tests(args.suite)
    total_per_run = len(singles) + len(multis)

    all_model_summaries = []
    root_out = Path(args.out)

    for model_idx, model in enumerate(models, start=1):
        print("\n" + "#" * 72)
        print(f"MODEL {model_idx}/{len(models)}: {model}")
        print("#" * 72)
        print(f"Endpoint: {base_url}")
        print(f"Suite: {args.suite} ({total_per_run} tests x {args.runs} run(s))")
        print("Shell commands are NOT executed.")

        results: List[dict] = []
        n = 0
        total = total_per_run * args.runs

        stop_model = False
        for run_idx in range(1, args.runs + 1):
            for test in singles:
                n += 1
                print(f"[{n:3d}/{total}] run {run_idx} {test.id} ... ", end="", flush=True)
                r = run_single(base_url, model, test, args.temperature, seed,
                               args.timeout, args.max_tokens)
                r["run"] = run_idx
                r["model"] = model
                results.append(r)
                print("PASS" if r["passed"] else f"FAIL ({r['failure_class']})")
                if args.stop_on_server_error and r["failure_class"].startswith("server_error"):
                    stop_model = True
                    break
            if stop_model:
                break

            for spec in multis:
                n += 1
                print(f"[{n:3d}/{total}] run {run_idx} {spec.id} ... ", end="", flush=True)
                r = run_multi(base_url, model, spec, args.temperature, seed,
                              args.timeout, args.max_tokens)
                r["run"] = run_idx
                r["model"] = model
                results.append(r)
                print("PASS" if r["passed"] else f"FAIL ({r['failure_class']})")
                if args.stop_on_server_error and r["failure_class"].startswith("server_error"):
                    stop_model = True
                    break
            if stop_model:
                break

        safe_model = re.sub(r"[^A-Za-z0-9._-]+", "_", model).strip("_") or f"model_{model_idx}"
        model_out = root_out.parent / f"{root_out.name}__{safe_model}"

        metadata = {
            "model": model,
            "all_models_tested": models,
            "base_url": base_url,
            "suite": args.suite,
            "runs": args.runs,
            "temperature": args.temperature,
            "seed": seed,
            "max_tokens": args.max_tokens,
            "generated_unix": time.time(),
            "python": sys.version,
            "platform": sys.platform,
            "note": "Shell commands generated by the model were never executed.",
        }

        json_path = model_out.with_suffix(".json")
        csv_path = model_out.with_suffix(".csv")
        write_json(json_path, metadata, results)
        write_csv(csv_path, results)
        print_summary(results, model, base_url, args.runs)
        print(f"\nJSON: {json_path}")
        print(f"CSV:  {csv_path}")

        passed = sum(1 for r in results if r["passed"])
        total_done = len(results)
        all_model_summaries.append({
            "model": model,
            "passed": passed,
            "total": total_done,
            "score_pct": (100.0 * passed / total_done) if total_done else 0.0,
            "json": str(json_path),
            "csv": str(csv_path),
        })

    if len(all_model_summaries) > 1:
        print("\n" + "=" * 72)
        print("ALL-MODEL SUMMARY")
        print("=" * 72)
        for row in sorted(all_model_summaries, key=lambda x: (-x["score_pct"], x["model"])):
            print(f"{row['model'][:44]:44s} {row['passed']:4d}/{row['total']:<4d} {row['score_pct']:6.1f}%")

        summary_path = root_out.parent / f"{root_out.name}__summary.json"
        summary_path.parent.mkdir(parents=True, exist_ok=True)
        summary_path.write_text(json.dumps({
            "base_url": base_url,
            "suite": args.suite,
            "runs": args.runs,
            "models": all_model_summaries,
        }, indent=2), encoding="utf-8")
        print(f"\nSummary JSON: {summary_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
