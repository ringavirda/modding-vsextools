#!/usr/bin/env python3
"""Client-branch gate for the mod assemblies.

Every production block guarded by a client-side test must have a covered line in the cobertura
report, or an allowlist entry giving the reason no headless test reaches it. A test that hands the
server API to client code runs the server branch and passes; this gate is what notices.

Usage: python side_gate_coverage.py <coverage.xml> <floors.json> [allowlist.json]

The source files scanned are those the report lists under the packages named in floors.json (the
same assemblies coverage_gate.py gates), so a sibling assembly built from source is not scanned
twice. Paths print relative to the working directory.

A client block is one of:
- the body of an `if` whose condition holds only on the client: `Side == EnumAppSide.Client`,
  `Side != EnumAppSide.Server`, `x.IsClient()`, `x.IsClient`, `!x.IsServer()`, `!x.IsServer`, alone
  or as a term of a top-level `&&` or `||`;
- the rest of the enclosing block after `if (<server test>) <exit>;`, where the exit is `break`,
  `continue`, or `return` of nothing, a literal, a name or a tuple of them, and a server test is
  `Side == EnumAppSide.Server`, `Side != EnumAppSide.Client`, `x.IsServer()`, `!x.IsClient()` and
  their property forms, alone or as a term of a top-level `||`;
- the `else` of an `if` whose condition holds only on the server.
The type tests count as side tests: `x is ICoreClientAPI [name]` and `x is not ICoreServerAPI` hold
only on the client, `x is ICoreServerAPI [name]` and `x is not ICoreClientAPI [name]` only on the
server.
A client body that is only `return;`, `break;` or `continue;` does no client work and is skipped;
a client body returning a value is an answer the engine acts on and counts.
Ternaries, property patterns (`is { Side: ... }`) and a bool local holding a side test are not
read.

A block is covered when any line strictly inside it has hits > 0. A block whose first line is also
the last line of its condition cannot be told apart from the condition and fails as unmappable.

allowlist.json is {"entries": [{"file": "<repo-relative path>", "member": "<Type>.<Member>",
"reason": "..."}]}; an entry allows every uncovered client block of that member. An entry with an
empty reason, or one that allows no uncovered block, fails the gate.

Exit 0 when every block is covered or allowed, 1 otherwise, 2 on a usage error. A gated source
file outside obj/ missing from disk, or a corpus with no client block, is a failure.
"""

import json
import os
import re
import sys
import xml.etree.ElementTree as ET

SIDE = r"[\w.?()\[\]]*Side"
OPERAND = r"[\w.?()\[\]]*"
CLIENT_TERMS = [
    re.compile(rf"^{SIDE}\s*==\s*EnumAppSide\.Client$"),
    re.compile(rf"^{SIDE}\s*!=\s*EnumAppSide\.Server$"),
    re.compile(rf"^{OPERAND}\.IsClient(\(\))?$"),
    re.compile(rf"^!\s*{OPERAND}\.IsServer(\(\))?$"),
    re.compile(rf"^{OPERAND}\s+is\s+ICoreClientAPI(\s+\w+)?$"),
    re.compile(rf"^{OPERAND}\s+is\s+not\s+ICoreServerAPI(\s+\w+)?$"),
]
SERVER_TERMS = [
    re.compile(rf"^{SIDE}\s*==\s*EnumAppSide\.Server$"),
    re.compile(rf"^{SIDE}\s*!=\s*EnumAppSide\.Client$"),
    re.compile(rf"^{OPERAND}\.IsServer(\(\))?$"),
    re.compile(rf"^!\s*{OPERAND}\.IsClient(\(\))?$"),
    re.compile(rf"^{OPERAND}\s+is\s+ICoreServerAPI(\s+\w+)?$"),
    re.compile(rf"^{OPERAND}\s+is\s+not\s+ICoreClientAPI(\s+\w+)?$"),
]
TYPE_HEADER = re.compile(r"\b(class|struct|record|interface)\s+(\w+)")
MEMBER_CALL = re.compile(r"(\w+)\s*(<[^<>]*>)?\s*\(")
KEYWORDS = {"if", "for", "foreach", "while", "switch", "using", "lock", "fixed", "catch", "typeof",
            "nameof", "default", "new", "base", "this", "where", "public", "private", "protected",
            "internal", "static", "override", "virtual", "async", "readonly", "unsafe", "extern",
            "sealed", "abstract", "partial"}
OPEN = {"(": ")", "[": "]", "{": "}"}


def mask(text):
    """Blanks comments and the contents of string and char literals, keeping every newline, so
    braces, parens and `if` in them are not read as code."""
    out = list(text)
    i, n = 0, len(text)

    def blank(a, b):
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
        elif c == "#" and not text[text.rfind("\n", 0, i) + 1:i].strip():
            j = text.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
        elif c == "'":
            j = i + 1
            while j < n and text[j] not in "'\n":
                j += 2 if text[j] == "\\" else 1
            j = min(j + 1, n)
            blank(i, j)
            i = j
        elif c == '"' or (c in "$@" and re.match(r"[$@]{1,3}\"", text[i:i + 4])):
            m = re.match(r"([$@]*)(\"+)", text[i:])
            assert m
            prefix, quotes = m.group(1), m.group(2)
            start = i + len(prefix)
            if len(quotes) >= 3:
                close = text.find(quotes, start + len(quotes))
                j = n if close < 0 else close + len(quotes)
            elif "@" in prefix:
                j = start + 1
                while j < n:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        break
                    j += 1
                j += 1
            else:
                j = start + 1
                while j < n and text[j] not in '"\n':
                    j += 2 if text[j] == "\\" else 1
                j += 1
            blank(i, j)
            i = j
        else:
            i += 1
    return "".join(out)


class Source:
    def __init__(self, text):
        self.text = mask(text)
        self.line_starts = [0] + [m.end() for m in re.finditer("\n", self.text)]
        self.match = {}
        self.parent = {}
        stack = []
        for i, c in enumerate(self.text):
            if c == "{":
                self.parent[i] = stack[-1] if stack else None
                stack.append(i)
            elif c == "}" and stack:
                self.match[stack.pop()] = i

    def line(self, pos):
        lo, hi = 0, len(self.line_starts) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if self.line_starts[mid] <= pos:
                lo = mid
            else:
                hi = mid - 1
        return lo + 1

    def skip_ws(self, pos):
        while pos < len(self.text) and self.text[pos].isspace():
            pos += 1
        return pos

    def close_of(self, pos):
        """The index of the bracket closing the one at pos."""
        depth, want = 0, OPEN[self.text[pos]]
        for i in range(pos, len(self.text)):
            c = self.text[i]
            if c == self.text[pos]:
                depth += 1
            elif c == want:
                depth -= 1
                if depth == 0:
                    return i
        return len(self.text) - 1

    def statement_end(self, pos):
        """The index of the last character of the statement starting at pos."""
        t = self.text
        if t[pos] == "{":
            return self.close_of(pos)
        m = re.match(r"(if|for|foreach|while|using|lock|fixed|switch)\s*\(", t[pos:])
        if m:
            paren = self.close_of(pos + m.end() - 1)
            if m.group(1) == "switch":
                return self.close_of(self.skip_ws(paren + 1))
            end = self.statement_end(self.skip_ws(paren + 1))
            if m.group(1) == "if":
                nxt = self.skip_ws(end + 1)
                if re.match(r"else\b", t[nxt:]):
                    end = self.statement_end(self.skip_ws(nxt + 4))
            return end
        i = pos
        while i < len(t):
            c = t[i]
            if c in OPEN:
                i = self.close_of(i) + 1
                continue
            if c == ";":
                return i
            i += 1
        return len(t) - 1

    def enclosing(self, pos):
        """The innermost brace containing pos, or None."""
        best = None
        for o, c in self.match.items():
            if o < pos <= c and (best is None or o > best):
                best = o
        return best

    def header(self, brace):
        i = brace - 1
        while i >= 0 and self.text[i] not in "{};":
            i -= 1
        lines = [ln for ln in self.text[i + 1:brace].split("\n") if not ln.strip().startswith("#")]
        h = " ".join(" ".join(lines).split())
        while True:
            stripped = re.sub(r"^\[[^\[\]]*(\[[^\[\]]*\][^\[\]]*)*\]\s*", "", h)
            if stripped == h:
                return h
            h = stripped

    def member(self, pos):
        """`Type.Member` for the member holding pos: the child of its innermost type body."""
        chain = []
        b = self.enclosing(pos)
        while b is not None:
            chain.append(b)
            b = self.parent[b]
        chain.reverse()
        type_name, member_brace = "?", None
        for k, b in enumerate(chain):
            m = TYPE_HEADER.search(self.header(b))
            if m:
                type_name = m.group(2)
                member_brace = chain[k + 1] if k + 1 < len(chain) else None
        if member_brace is None:
            return type_name
        h = self.header(member_brace)
        if "=" in h and "(" not in h.split("=")[0]:
            h = h.split("=")[0]
        names = [m.group(1) for m in MEMBER_CALL.finditer(h)
                 if m.group(1) not in KEYWORDS and depth_at(h, m.start()) == 0]
        if names:
            return f"{type_name}.{names[-1] if ':' not in h else names[0]}"
        words = re.findall(r"\w+", h)
        return f"{type_name}.{words[-1] if words else '?'}"


def depth_at(text, pos):
    return sum(1 if c == "(" else -1 for c in text[:pos] if c in "()")


def split_top(cond, op):
    parts, depth, start, i = [], 0, 0, 0
    while i < len(cond):
        c = cond[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif depth == 0 and cond.startswith(op, i):
            parts.append(cond[start:i])
            start = i + len(op)
            i += len(op)
            continue
        i += 1
    parts.append(cond[start:])
    return [" ".join(p.split()) for p in parts]


def strip_parens(term):
    while term.startswith("(") and term.endswith(")"):
        term = term[1:-1].strip()
    return term


def any_term(terms, patterns):
    return any(p.match(strip_parens(t)) for t in terms for p in patterns)


def classify(cond):
    """('client', 'server' or None) for an if-condition: client when its body can only run with a
    client term true or runs whenever one is; server when a server term alone, or in a top-level
    disjunction, makes an exit in the body leave only the client behind."""
    ors = split_top(cond, "||")
    if len(ors) > 1:
        if any_term(ors, CLIENT_TERMS):
            return "client", False
        if any_term(ors, SERVER_TERMS):
            return "server", True
        return None, False
    ands = split_top(cond, "&&")
    if any_term(ands, CLIENT_TERMS):
        return "client", False
    if any_term(ands, SERVER_TERMS):
        return "server", len(ands) == 1
    return None, False


def body_of(src, start, end):
    body = src.text[start:end + 1].strip()
    if body.startswith("{") and body.endswith("}"):
        body = body[1:-1].strip()
    return body


def is_bare_exit(src, start, end):
    """A client body that does no client work: only `return;`, `break;` or `continue;`."""
    return bool(re.fullmatch(r"(return|break|continue)\s*;", body_of(src, start, end)))


def is_exit(src, start, end):
    """A server body that leaves the block: `break`, `continue`, or `return` of nothing, a literal,
    a name or a tuple of them."""
    value = r"(true|false|null|default|[\w.]+|-?\d+)"
    return bool(re.fullmatch(
        rf"(return(\s+{value}|\s*\(\s*{value}(\s*,\s*{value})*\s*\))?|break|continue)\s*;",
        body_of(src, start, end)))


def span(src, start, end):
    """(first line, last line) of the code in [start, end], inside the braces when braced; None
    when empty."""
    if src.text[start] == "{":
        start, end = start + 1, end - 1
    body = src.text[start:end + 1]
    if not body.strip():
        return None
    first = start + (len(body) - len(body.lstrip()))
    last = start + len(body.rstrip()) - 1
    return src.line(first), src.line(last)


def find_blocks(text):
    """Every client block in one C# source text, as dicts of member, first, last, cond_line and
    form. Pure: reads nothing but its argument."""
    src = Source(text)
    blocks = []
    for m in re.finditer(r"\bif\s*\(", src.text):
        paren = m.end() - 1
        close = src.close_of(paren)
        side, exclusive = classify(src.text[paren + 1:close])
        if side is None:
            continue
        body = src.skip_ws(close + 1)
        body_end = src.statement_end(body)
        cond_line = src.line(close)
        found = []
        if side == "client" and not is_bare_exit(src, body, body_end):
            found.append(("then", body, body_end))
        if side == "server":
            if is_exit(src, body, body_end):
                owner = src.enclosing(m.start())
                if owner is not None:
                    rest = src.skip_ws(body_end + 1)
                    if rest < src.match[owner]:
                        found.append(("rest", rest, src.match[owner] - 1))
            nxt = src.skip_ws(body_end + 1)
            if exclusive and re.match(r"else\b", src.text[nxt:]):
                else_body = src.skip_ws(nxt + 4)
                found.append(("else", else_body, src.statement_end(else_body)))
        for form, a, b in found:
            lines = span(src, a, b)
            if lines is None:
                continue
            blocks.append({
                "member": src.member(m.start()),
                "first": lines[0],
                "last": lines[1],
                "cond_line": cond_line,
                "form": form,
            })
    return blocks


def load_hits(path, packages):
    """{source filename: {line: max hits}} over the classes of the named packages."""
    hits = {}
    current = None
    file_hits = {}
    for event, el in ET.iterparse(path, events=("start", "end")):
        if event == "start":
            if el.tag == "package":
                current = el.get("name")
            elif el.tag == "class" and current in packages:
                file_hits = hits.setdefault(el.get("filename"), {})
            continue
        if el.tag == "line" and current in packages:
            n = int(el.get("number"))
            file_hits[n] = max(file_hits.get(n, 0), int(el.get("hits")))
        elif el.tag in ("class", "package"):
            el.clear()
    return hits


def load_allowlist(path):
    if not path or not os.path.exists(path):
        return []
    with open(path) as f:
        return json.load(f).get("entries", [])


def display(path):
    rel = os.path.relpath(path)
    return (path if rel.startswith("..") else rel).replace(os.sep, "/")


def main(cov_path, floors_path, allow_path):
    with open(floors_path) as f:
        packages = set(json.load(f)["assemblies"])
    hits = load_hits(cov_path, packages)
    entries = load_allowlist(allow_path)
    used = [0] * len(entries)
    failures = []
    for k, e in enumerate(entries):
        if not str(e.get("reason", "")).strip():
            failures.append(f"allowlist entry {e.get('file')} {e.get('member')} has no reason")

    total = covered = allowed = 0
    for filename in sorted(hits):
        if not os.path.exists(filename):
            if "/obj/" not in filename.replace(os.sep, "/"):
                failures.append(f"{display(filename)}: in the report but not on disk")
            continue
        with open(filename, encoding="utf-8-sig") as f:
            blocks = find_blocks(f.read())
        file_hits = hits[filename]
        shown = display(filename)
        norm = filename.replace(os.sep, "/")
        for b in blocks:
            total += 1
            where = f"{shown}:{b['first']} {b['member']} ({b['form']})"
            if b["first"] == b["cond_line"]:
                failures.append(f"{where}: shares line {b['first']} with its condition - unmappable")
                continue
            if any(file_hits.get(n, 0) > 0 for n in range(b["first"], b["last"] + 1)):
                covered += 1
                continue
            match = [k for k, e in enumerate(entries)
                     if norm.endswith("/" + e.get("file", "").lstrip("/")) and e.get("member") == b["member"]]
            if match:
                allowed += 1
                for k in match:
                    used[k] += 1
                continue
            failures.append(f"{where}: no covered line and no allowlist entry")

    for k, e in enumerate(entries):
        if used[k] == 0:
            failures.append(f"allowlist entry {e.get('file')} {e.get('member')} allows no uncovered client block")

    if total == 0:
        failures.append("no client blocks found - the report names no gated source on disk")
    print(f"side gates: {total} client blocks, {covered} covered, {allowed} allowed")
    if failures:
        print("\nSIDE GATE FAILED:")
        for line in failures:
            print(f"  - {line}")
        return 1
    return 0


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4):
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) == 4 else None))
