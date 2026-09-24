"""Tests for side_gate_coverage.py: python3 -m unittest discover -s tools -p 'test_*.py'"""

import contextlib
import io
import json
import os
import tempfile
import textwrap
import unittest

import side_gate_coverage as gate


def blocks(body, members="  void M() {\n%s\n  }\n"):
    source = "class T {\n" + members % textwrap.indent(textwrap.dedent(body), "    ") + "}\n"
    return gate.find_blocks(source)


class FinderTests(unittest.TestCase):
    def test_a_side_equals_client_body_is_a_block(self):
        found = blocks("""\
            if (Api.Side == EnumAppSide.Client)
              Draw();
            """)
        self.assertEqual([("T.M", "then", 4, 4)], [(b["member"], b["form"], b["first"], b["last"]) for b in found])

    def test_the_isclient_extension_and_property_are_client_tests(self):
        found = blocks("""\
            if (api.IsClient()) {
              Draw();
            }
            if (interaction.IsClient && ready)
              Draw();
            """)
        self.assertEqual(["then", "then"], [b["form"] for b in found])

    def test_a_not_server_body_doing_client_work_is_a_block(self):
        found = blocks("""\
            if (world.Side != EnumAppSide.Server) {
              Play();
              return true;
            }
            """)
        self.assertEqual([("then", 4, 5)], [(b["form"], b["first"], b["last"]) for b in found])

    def test_a_bare_client_exit_is_not_a_block(self):
        found = blocks("""\
            if (world.Side != EnumAppSide.Server)
              return;
            foreach (var slot in slots) {
              if (Api.Side == EnumAppSide.Client) { continue; }
              if (Api.IsClient()) break;
            }
            Work();
            """)
        self.assertEqual([], found)

    def test_a_client_answer_is_a_block_whatever_it_returns(self):
        found = blocks("""\
            if (Api.Side == EnumAppSide.Client)
              return true;
            if (api == null || api.Side != EnumAppSide.Server)
              return (null, null);
            if (Api.Side == EnumAppSide.Client)
              return StateOf(slot) == Sand;
            """)
        self.assertEqual([4, 6, 8], [b["first"] for b in found])

    def test_a_server_exit_returning_a_value_still_leaves_the_client_behind(self):
        found = blocks("""\
            if (world.Side == EnumAppSide.Server)
              return true;
            Play();
            return true;
            """)
        self.assertEqual([("rest", 5, 6)], [(b["form"], b["first"], b["last"]) for b in found])

    def test_a_client_type_test_body_is_a_block(self):
        found = blocks("""\
            if (Api is ICoreClientAPI capi) {
              Report(capi);
            }
            if (api is not ICoreServerAPI && ready)
              Draw();
            """)
        self.assertEqual([("then", 4, 4), ("then", 7, 7)],
                         [(b["form"], b["first"], b["last"]) for b in found])

    def test_a_server_type_test_leaves_its_rest_and_else_to_the_client(self):
        found = blocks("""\
            if (sapi is ICoreServerAPI s)
              Tick(s);
            else
              Draw();
            if (_be.Api is not ICoreClientAPI || _animatable == null)
              return;
            Animate();
            """)
        self.assertEqual([("else", 6, 6), ("rest", 9, 9)],
                         [(b["form"], b["first"], b["last"]) for b in found])

    def test_the_rest_after_a_server_exit_is_a_block(self):
        found = blocks("""\
            if (Api?.Side != EnumAppSide.Client || stale)
              return;
            Snapshot();
            Redraw();
            """)
        self.assertEqual([("rest", 5, 6)], [(b["form"], b["first"], b["last"]) for b in found])

    def test_the_else_of_a_server_test_is_a_block(self):
        found = blocks("""\
            if (api.Side == EnumAppSide.Server)
              Tick();
            else {
              Draw();
            }
            """)
        self.assertEqual([("else", 6, 6)], [(b["form"], b["first"], b["last"]) for b in found])

    def test_side_tests_in_comments_and_strings_are_not_read(self):
        found = blocks("""\
            // if (Api.Side == EnumAppSide.Client) { Draw(); }
            Log("if (Api.Side == EnumAppSide.Client) { Draw(); }");
            Log($@"if (x.IsClient) {{ Draw(); }}");
            """)
        self.assertEqual([], found)

    def test_the_member_is_named_past_a_tuple_return_attributes_and_default_calls(self):
        found = blocks("""\
            if (Api.Side == EnumAppSide.Client)
              Draw();
            """, members="  [Attr(typeof(X))]\n  private (int A, int B) Read(int n = Count()) {\n%s\n  }\n")
        self.assertEqual(["T.Read"], [b["member"] for b in found])


REPORT = """<?xml version="1.0"?>
<coverage><packages>
  <package name="mod"><classes>
    <class name="T" filename="{src}"><methods><method name="M"><lines>
      {lines}
    </lines></method></methods></class>
  </classes></package>
  <package name="other"><classes>
    <class name="U" filename="{src}"><lines><line number="4" hits="9" /></lines></class>
    <class name="V" filename="{other}"><lines><line number="4" hits="0" /></lines></class>
  </classes></package>
</packages></coverage>
"""

SOURCE = """class T {
  void M() {
    if (Api.Side == EnumAppSide.Client)
      Draw();
  }
}
"""


class GateTests(unittest.TestCase):
    def run_gate(self, hits, entries=None, source=SOURCE, missing=None):
        with tempfile.TemporaryDirectory() as d:
            src = os.path.join(d, "T.cs")
            with open(src, "w") as f:
                f.write(source)
            other = os.path.join(d, "V.cs")
            with open(other, "w") as f:
                f.write(SOURCE)
            cov = os.path.join(d, "coverage.xml")
            lines = "".join(f'<line number="{n}" hits="{h}" />' for n, h in hits.items())
            report = REPORT.format(src=src, other=other, lines=lines)
            if missing is not None:
                gone = os.path.join(d, missing)
                report = report.replace("  </classes></package>\n  <package name=\"other\">",
                                        f'    <class name="G" filename="{gone}"><lines>'
                                        '<line number="1" hits="1" /></lines></class>\n'
                                        '  </classes></package>\n  <package name="other">')
            with open(cov, "w") as f:
                f.write(report)
            floors = os.path.join(d, "floors.json")
            with open(floors, "w") as f:
                json.dump({"assemblies": {"mod": {}}}, f)
            allow = None
            if entries is not None:
                allow = os.path.join(d, "allow.json")
                with open(allow, "w") as f:
                    json.dump({"entries": entries}, f)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = gate.main(cov, floors, allow)
            return code, out.getvalue()

    def test_an_uncovered_block_fails_naming_its_file_and_member_and_other_packages_are_not_read(self):
        code, out = self.run_gate({3: 1, 4: 0})
        self.assertEqual(1, code)
        self.assertIn("T.cs:4 T.M (then): no covered line", out)
        self.assertIn("side gates: 1 client blocks, 0 covered, 0 allowed", out)

    def test_a_covered_block_passes(self):
        self.assertEqual(0, self.run_gate({3: 1, 4: 2})[0])

    def test_an_allowlisted_block_passes(self):
        code, out = self.run_gate({4: 0}, [{"file": "T.cs", "member": "T.M", "reason": "Q8"}])
        self.assertEqual(0, code)
        self.assertIn("1 client blocks, 0 covered, 1 allowed", out)

    def test_an_entry_without_a_reason_fails(self):
        code, out = self.run_gate({4: 0}, [{"file": "T.cs", "member": "T.M", "reason": " "}])
        self.assertEqual(1, code)
        self.assertIn("has no reason", out)

    def test_an_entry_allowing_nothing_fails(self):
        code, out = self.run_gate({4: 1}, [{"file": "T.cs", "member": "T.M", "reason": "Q8"}])
        self.assertEqual(1, code)
        self.assertIn("T.cs T.M allows no uncovered client block", out)

    def test_a_block_sharing_its_conditions_line_fails_as_unmappable(self):
        source = "class T {\n  void M() {\n    if (Api.Side == EnumAppSide.Client) Draw();\n  }\n}\n"
        code, out = self.run_gate({3: 1}, source=source)
        self.assertEqual(1, code)
        self.assertIn("unmappable", out)

    def test_a_gated_source_missing_from_disk_fails_unless_it_is_generated(self):
        code, out = self.run_gate({3: 1, 4: 2}, missing="Gone.cs")
        self.assertEqual(1, code)
        self.assertIn("Gone.cs: in the report but not on disk", out)
        self.assertEqual(0, self.run_gate({3: 1, 4: 2}, missing="obj/Gen.g.cs")[0])

    def test_a_corpus_with_no_client_block_fails(self):
        code, out = self.run_gate({3: 1}, source="class T {\n  void M() {\n    Work();\n  }\n}\n")
        self.assertEqual(1, code)
        self.assertIn("side gates: 0 client blocks", out)
        self.assertIn("no client blocks found", out)


if __name__ == "__main__":
    unittest.main()
