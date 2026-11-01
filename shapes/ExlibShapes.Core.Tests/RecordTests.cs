using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExpandedLib.Shapes.Core.Tests;

public class RecordTests {
  private const string ShapeText = """
    { "textures": {}, "elements": [
      { "name": "Base", "from": [0,0,0], "to": [4,4,4], "children": [
        { "name": "Bolt1", "from": [1,4,1], "to": [2,5,2] },
        { "name": "Pin", "from": [2,4,2], "to": [3,9,3] } ] },
      { "name": "Lug", "from": [4,0,0], "to": [6,2,2] } ] }
    """;

  private const string RecordText = """
    {
      "schema": 1, "shape": "part.json", "shapeSha": "abc",
      "kinds": ["casting"], "owner": "generated",
      "elements": {
        "Base": { "stage": "cast", "role": "cast", "family": "b1", "futureKey": 7 },
        "Bolt1": { "stage": "fastener", "role": "fastener" },
        "Pin": { "stage": "fastener", "role": "fastener", "rod": true },
        "Lug": { "stage": "cast", "role": "cast", "inMachine": true }
      },
      "features": { "ear": { "elements": ["Lug"], "origin": "cast", "form": "ear", "faces": ["Lug/east"], "step": "drill", "sliding": true, "extra": [1] } },
      "steps": [ { "id": "drill", "tool": "drillpress", "hand": "handdrill", "faces": ["Lug/east"], "note": "x" } ],
      "cells": [[0,0,0]], "clips": { "spin": "Repeat" }, "ratings": { "atm": 4.0 },
      "baselines": { "facecheck": { "count": 12 } },
      "answers": [ { "feature": "ear", "question": "q", "answer": "a", "by": "fallen", "date": "2026-11-01", "featureSha": "ff", "why": "z" } ],
      "held": { "kind": "long", "grips": [[1,2,3]] },
      "futureSlot": { "k": 1 }
    }
    """;

  private static PartRecord Record() => PartRecord.Read(RecordText);

  [Fact]
  public void Unknown_field_survives_round_trip() {
    JObject back = JObject.Parse(PartRecord.Read(Record().Write()).Write());
    Assert.Equal(1, back["futureSlot"]!["k"]!.Value<int>());
    Assert.Equal("long", back["held"]!["kind"]!.Value<string>());
    Assert.Equal(7, back["elements"]!["Base"]!["futureKey"]!.Value<int>());
    Assert.Equal(1, back["features"]!["ear"]!["extra"]![0]!.Value<int>());
    Assert.Equal("x", back["steps"]![0]!["note"]!.Value<string>());
    Assert.Equal("z", back["answers"]![0]!["why"]!.Value<string>());
  }

  [Fact]
  public void Typed_fields_are_read() {
    PartRecord r = Record();
    Assert.Equal("generated", r.Owner);
    Assert.Equal("ear", r.Features["ear"].Form);
    Assert.True(r.Features["ear"].Sliding);
    Assert.Equal("handdrill", r.Steps[0].Hand);
    Assert.Equal("drillpress", r.Steps[0].Tool);
    Assert.Equal("Repeat", r.Clips["spin"]);
    Assert.Equal("fallen", r.Answers[0].By);
    Assert.Equal(12, r.Baselines["facecheck"]["count"]!.Value<int>());
  }

  [Fact]
  public void Write_is_stable() {
    string once = Record().Write();
    Assert.Equal(once, PartRecord.Read(once).Write());
  }

  [Fact]
  public void Other_schema_is_refused() {
    Assert.Throws<JsonException>(() =>
      PartRecord.Read(RecordText.Replace("\"schema\": 1", "\"schema\": 2"))
    );
    Assert.Throws<JsonException>(() =>
      PartRecord.Read(
        RecordText.Replace("\"kinds\": [\"casting\"]", "\"kinds\": 3")
      )
    );
  }

  [Fact]
  public void Matching_names_reconcile_and_report_the_sha() {
    Shape shape = Reader.Read(ShapeText);
    Assert.True(Reconcile.Check(Record(), shape, "abc"));
    Assert.False(Reconcile.Check(Record(), shape, "def"));
  }

  [Fact]
  public void Renamed_element_is_dangling() {
    Shape shape = Reader.Read(ShapeText.Replace("\"Lug\"", "\"Ear\""));
    ReconcileException e = Assert.Throws<ReconcileException>(() =>
      Reconcile.Check(Record(), shape, "abc")
    );
    Assert.Equal(["Lug"], e.Dangling);
    Assert.Equal(["Ear"], e.New);
    Assert.Contains("Lug", e.Message);
    Assert.Contains("Ear", e.Message);
  }

  [Fact]
  public void Added_element_is_new() {
    Shape shape = Reader.Read(
      ShapeText.Replace(
        "{ \"name\": \"Lug\"",
        "{ \"name\": \"Rib\", \"from\": [0,0,0], \"to\": [1,1,1] }, { \"name\": \"Lug\""
      )
    );
    ReconcileException e = Assert.Throws<ReconcileException>(() =>
      Reconcile.Check(Record(), shape, "abc")
    );
    Assert.Empty(e.Dangling);
    Assert.Equal(["Rib"], e.New);
  }

  [Fact]
  public void Count_query_reports_parts_fasteners_rods() {
    PartCount c = Record().Count();
    Assert.Equal(2, c.Parts);
    Assert.Equal(1, c.Fasteners);
    Assert.Equal(1, c.Rods);
    Assert.Equal(2, c.ByStageRole["cast/cast"]);
    Assert.Equal(2, c.ByStageRole["fastener/fastener"]);
  }

  [Fact]
  public void A_record_is_always_writable() {
    string dir = Directory.CreateTempSubdirectory().FullName;
    try {
      string path = Path.Combine(dir, "part.part.json");
      var store = new Provenance(Path.Combine(dir, "provenance.jsonl"));
      Assert.Empty(store.Rows);
      Record().WriteFile(path);
      File.WriteAllText(path, "edited by hand");
      Record().WriteFile(path);
      Assert.Equal(Record().Write(), File.ReadAllText(path));
      Assert.Equal(Record().Write(), PartRecord.ReadFile(path).Write());
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
