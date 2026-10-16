using System.Text.Json;

namespace Missum.Core.Coding;

/// <summary>One offline, source-bound Lean checker for native projects and the research runner.</summary>
public static class LeanProofHarness
{
    public const string LeanVersion = "4.30.0";

    /// <summary>Execute with Python and the selected research work directory as CWD.</summary>
    public static string CreateScript(JsonElement arguments) =>
        "CONFIG_JSON = " + JsonSerializer.Serialize(arguments.GetRawText()) + "\n" + Script;

    private const string Script = """"
        import hashlib, json, os, pathlib, re, shutil, subprocess, sys, time, uuid
        # Native Windows Python may inherit a legacy console encoding even though
        # the command bridge reads UTF-8. Compiler diagnostics and theorem types
        # contain Unicode; they must never turn a real failure into empty stdout.
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
        CONFIG = json.loads(CONFIG_JSON)
        PREPARE_ONLY = "--missum-prepare-only" in sys.argv[1:]
        VERSION = "4.30.0"
        CONTRACT = "declarative-lean-audited-v2"
        STARTED = time.monotonic()
        DEADLINE = STARTED + int(CONFIG.get("timeoutSeconds", 300))
        ROOT = pathlib.Path.cwd().resolve()
        PROOF_ROOT = ROOT / "lean"
        RESULT = {"success": False, "projectId": CONFIG["projectId"], "operation": CONFIG.get("operation", "check"),
                  "formalVerification": "NotEstablished", "sourceContract": CONTRACT, "leanVersion": VERSION,
                  "projectDirectory": "lean", "networkAccess": "not-required", "diagnostics": []}

        def atomic_write(path, text):
            path.parent.mkdir(parents=True, exist_ok=True)
            tmp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
            try:
                tmp.write_text(text, encoding="utf-8", newline="\n")
                tmp.replace(path)
            finally:
                tmp.unlink(missing_ok=True)

        def safe_path(relative, must_exist=False):
            if not isinstance(relative, str) or not relative or len(relative) > 1024 or ":" in relative or "\\" in relative:
                raise ValueError("A relative project path using / is required.")
            path = ROOT / relative
            if pathlib.Path(relative).is_absolute() or any(p in ("..", ".git") for p in pathlib.Path(relative).parts):
                raise ValueError("The proof path must stay inside the selected project.")
            cursor = path
            while cursor != ROOT:
                if cursor.is_symlink():
                    raise ValueError("Linked proof paths are not accepted.")
                cursor = cursor.parent
            path = path.resolve(strict=must_exist)
            if ROOT not in path.parents:
                raise ValueError("The proof path leaves the selected project.")
            if path.suffix != ".lean":
                raise ValueError("A .lean source file is required.")
            return path

        def run(command, cwd, env):
            remaining = DEADLINE - time.monotonic()
            if remaining <= 0:
                raise subprocess.TimeoutExpired(command, 0)
            completed = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True,
                                       encoding="utf-8", errors="replace", check=False, timeout=remaining)
            return {"command": command, "exitCode": completed.returncode,
                    "stdout": completed.stdout, "stderr": completed.stderr}

        def validate(source):
            if not source.strip() or len(source) > 64000:
                raise ValueError("Lean source must contain 1 to 64000 characters.")
            if any(c in source for c in "#«»"):
                raise ValueError("Directives and escaped identifiers are not accepted in declarative proof sources.")
            def trusted_import(match):
                modules = match.group(1).split()
                if not modules or any(re.fullmatch(r"(?:Mathlib|Std|Init)(?:\.[A-Za-z0-9_]+)*", m) is None for m in modules):
                    raise ValueError("Only installed Mathlib, Std and Init modules may be imported.")
                return ""
            stripped = re.sub(r"(?m)^[ \t]*import[ \t]+([^\r\n]+)[ \t]*\r?$", trusted_import, source)
            forbidden = {"sorry", "sorryAx", "admit", "axiom", "constant", "set_option", "import", "prelude",
                         "run_cmd", "run_tac", "run_elab", "elab", "elab_rules", "macro", "macro_rules", "syntax",
                         "initialize", "builtin_initialize", "attribute", "IO", "System", "Lean", "unsafe", "extern",
                         "implemented_by", "register_option", "simproc", "builtin_simproc", "native_decide", "ofReduceBool",
                         "ofReduceNat", "export", "notation", "infix", "infixl", "infixr", "prefix", "postfix"}
            bad = forbidden.intersection(re.findall(r"[^\W]+", stripped, re.UNICODE))
            if bad:
                raise ValueError("Unsupported declaration or proof admission: " + ", ".join(sorted(bad)))
            return stripped

        def name_examples(source):
            # Rename anonymous examples only outside comments and strings. Their kernel
            # declarations then remain available for transitive axiom inspection.
            result, index, depth, in_string, count = [], 0, 0, False, 0
            while index < len(source):
                if depth:
                    if source.startswith("/-", index): depth += 1; result.append("/-"); index += 2
                    elif source.startswith("-/", index): depth -= 1; result.append("-/"); index += 2
                    else: result.append(source[index]); index += 1
                elif in_string:
                    char = source[index]; result.append(char); index += 1
                    if char == "\\" and index < len(source): result.append(source[index]); index += 1
                    elif char == '"': in_string = False
                elif source.startswith("--", index):
                    end = source.find("\n", index)
                    if end < 0: end = len(source)
                    result.append(source[index:end]); index = end
                elif source.startswith("/-", index): depth = 1; result.append("/-"); index += 2
                elif source[index] == '"': in_string = True; result.append('"'); index += 1
                elif source.startswith("example", index) and (index == 0 or not (source[index-1].isalnum() or source[index-1] in "_'")) and (index+7 == len(source) or not (source[index+7].isalnum() or source[index+7] in "_'")):
                    count += 1; result.append("theorem missumExample" + str(count)); index += 7
                else: result.append(source[index]); index += 1
            return "".join(result)

        def main():
            operation = RESULT["operation"]
            if operation not in ("initialize", "check"):
                raise ValueError("operation must be initialize or check.")
            use_mathlib = CONFIG.get("useMathlib", True)
            if type(use_mathlib) is not bool:
                raise ValueError("useMathlib must be a boolean.")
            RESULT["useMathlib"] = use_mathlib
            # Never run a project's own Lake configuration. Only a fixed compiler and
            # provisioned standard-library roots participate in the trusted environment.
            compiler = os.environ.get("MISSUM_LEAN_COMPILER") or shutil.which("lean")
            if not compiler:
                RESULT.update(blocked=True, missingDependency="Lean 4.30.0", message="Install the pinned Lean toolchain before using the proof tool.")
                return 127
            env = os.environ.copy()
            env.pop("LEAN_PATH", None)
            env.pop("LEAN_SRC_PATH", None)
            env["ELAN_TOOLCHAIN"] = "leanprover/lean4:v" + VERSION
            version = run([compiler, "--version"], ROOT, env)
            RESULT.update(compiler=compiler, version=version["stdout"].strip())
            if version["exitCode"] != 0 or not re.search(r"\b" + re.escape(VERSION) + r"\b", version["stdout"]):
                RESULT.update(blocked=True, message="The installed compiler does not match pinned Lean " + VERSION, toolchainDiagnostic=version)
                return 127
            mathlib = None
            library_roots = []
            if use_mathlib:
                candidates = [os.environ.get("MISSUM_LEAN_MATHLIB_ROOT"), "/opt/mathlib",
                              str(pathlib.Path.home() / ".cache" / "Missum" / "lean" / ("mathlib-v" + VERSION))]
                mathlib = next((pathlib.Path(p).resolve() for p in candidates if p and (pathlib.Path(p)/".lake/build/lib/lean/Mathlib.olean").is_file()), None)
                if mathlib is None:
                    RESULT.update(blocked=True, missingDependency="precompiled Mathlib for Lean " + VERSION,
                                  message="Provision Mathlib once; set MISSUM_LEAN_MATHLIB_ROOT. Proof checks never download dependencies.")
                    return 127
                toolchain = (mathlib/"lean-toolchain").read_text(encoding="utf-8").strip()
                if toolchain != "leanprover/lean4:v" + VERSION:
                    raise ValueError("Mathlib and Lean toolchain versions do not match.")
                library_roots = [mathlib/".lake/build/lib/lean"]
                library_roots += sorted(p for p in (mathlib/".lake/packages").glob("*/.lake/build/lib/lean") if p.is_dir())
                env["LEAN_PATH"] = os.pathsep.join(str(p) for p in library_roots)
                RESULT["mathlibRoot"] = str(mathlib)
                manifest = mathlib/"lake-manifest.json"
                RESULT["mathlibManifestSha256"] = hashlib.sha256(manifest.read_bytes()).hexdigest() if manifest.is_file() else None
                revision = mathlib/"missum-mathlib-revision"
                if not revision.is_file(): revision = mathlib/"missum-revision"
                RESULT["mathlibRevision"] = revision.read_text(encoding="utf-8").strip() if revision.is_file() else None
            if PROOF_ROOT.is_symlink():
                raise ValueError("The Lean project directory must not be a link.")
            PROOF_ROOT.mkdir(parents=True, exist_ok=True)
            atomic_write(PROOF_ROOT/"lean-toolchain", "leanprover/lean4:v" + VERSION + "\n")
            lake_config = 'name = "missum_proofs"\nversion = "0.1.0"\n'
            if mathlib:
                lake_config += '\n[[require]]\nname = "mathlib"\npath = ' + json.dumps(str(mathlib).replace("\\", "/")) + '\n'
            atomic_write(PROOF_ROOT/"lakefile.toml", lake_config)
            atomic_write(PROOF_ROOT/"README.md", "# Missum Lean project\n\nPinned Lean " + VERSION + ". Proof sources remain in this project.\n"
                         "Use Missum's Lean check for offline compiler diagnostics and a transitive axiom audit.\n"
                         "Kernel acceptance verifies the written statement under its explicit hypotheses; it does not verify physical assumptions or experimental evidence.\n")
            if operation == "initialize":
                RESULT.update(success=True, initialized=True, prepared=PREPARE_ONLY, message="Lean project initialized; no mathematical statement has been proved.")
                return 0
            source_value, source_path = CONFIG.get("source"), CONFIG.get("path")
            if source_value is not None and source_path is not None:
                raise ValueError("Use either source or path, not both.")
            if source_path is not None:
                source_file = safe_path(source_path, must_exist=True)
                with source_file.open("rb") as source_stream:
                    source_bytes = source_stream.read(256001)
                if len(source_bytes) > 256000:
                    raise ValueError("Lean source is too large.")
                # Decode and bind exactly one byte snapshot. Reading the content
                # and its hash separately could certify a concurrent file edit.
                source = source_bytes.decode("utf-8-sig")
            elif isinstance(source_value, str):
                source = source_value
                source_bytes = source.encode("utf-8")
                source_file = PROOF_ROOT / ("Proof" + hashlib.sha256(source.encode("utf-8")).hexdigest()[:16] + ".lean")
                # Validate before persisting untrusted source.
                validate(source)
                atomic_write(source_file, source)
            else:
                raise ValueError("A source string or existing project-relative .lean path is required.")
            body = name_examples(validate(source))
            source_hash = hashlib.sha256(source.encode("utf-8")).hexdigest()
            file_hash = hashlib.sha256(source_bytes).hexdigest()
            RESULT.update(source=str(source_file.relative_to(ROOT)).replace("\\", "/"), sourceSha256=source_hash, sourceFileSha256=file_hash, sourceText=source)
            audit = '''
        run_cmd do
          let env ← Lean.getEnv
          let names := env.constants.foldStage2 (fun names name ci =>
            match ci with
            | .thmInfo _ => names.push name
            | _ => names) (#[] : Array Lean.Name)
          let mut proofs : Array Lean.Json := #[]
          for name in names do
            let axioms ← Lean.collectAxioms name
            let some info := env.find? name | continue
            let statement ← Lean.Elab.Command.liftTermElabM do
              return (← Lean.Meta.ppExpr info.type).pretty
            proofs := proofs.push (Lean.Json.mkObj [("declaration", Lean.toJson name.toString), ("statement", Lean.toJson statement), ("axioms", Lean.toJson (axioms.map Lean.Name.toString))])
          Lean.logInfo ("MISSUM_AXIOM_AUDIT:" ++ (Lean.Json.mkObj [("proofs", Lean.toJson proofs)]).compress)
        '''
            # Source line offsets are disclosed and diagnostics retain exact generated path.
            prefix = "import Lean\n" + ("import Mathlib\n" if use_mathlib else "import Std\n")
            audit_source = prefix + body + "\n" + audit
            audit_hash = hashlib.sha256(audit_source.encode("utf-8")).hexdigest()
            audit_file = PROOF_ROOT / ("MissumAudit" + audit_hash[:24] + ".lean")
            atomic_write(audit_file, audit_source)
            RESULT.update(checkedSource=str(audit_file.relative_to(ROOT)).replace("\\", "/"),
                          checkedSourceSha256=audit_hash, sourceLineOffset=2)
            # First stage only materializes deterministic inputs. A sandbox run
            # can then freeze those files before the compiler reads them in the
            # second stage; no provenance exemption is needed for generated code.
            if PREPARE_ONLY:
                RESULT.update(success=True, prepared=True,
                              message="Lean sources prepared; the compiler has not checked the statement.")
                return 0
            checked = run([compiler, "-DwarningAsError=true", str(audit_file)], ROOT, env)
            RESULT.update(exitCode=checked["exitCode"], stdout=checked["stdout"], stderr=checked["stderr"])
            marker = "MISSUM_AXIOM_AUDIT:"
            audits = [json.loads(line.split(marker, 1)[1]) for line in checked["stdout"].splitlines() if marker in line]
            if checked["exitCode"] != 0:
                RESULT["diagnostics"] = (checked["stdout"] + "\n" + checked["stderr"]).strip().splitlines()
                return checked["exitCode"] or 1
            if len(audits) != 1 or not audits[0].get("proofs"):
                RESULT["message"] = "Compiler succeeded, but no theorem declaration was available for axiom inspection. Add a theorem or example."
                return 2
            proofs = audits[0]["proofs"]
            allowed_axioms = {"propext", "Classical.choice", "Quot.sound"}
            unexpected = sorted({axiom for proof in proofs for axiom in proof["axioms"] if axiom not in allowed_axioms})
            RESULT.update(proofs=proofs, allowedFoundationalAxioms=sorted(allowed_axioms), unexpectedAxioms=unexpected,
                          axiomAudit="passed" if not unexpected else "failed")
            if unexpected:
                RESULT["message"] = "The proof depends on unsupported axioms; kernel acceptance is withheld."
                return 2
            if hashlib.sha256(audit_file.read_bytes()).hexdigest() != audit_hash:
                RESULT.update(message="The checked Lean input changed during compilation; check again.", checkedSourceChanged=True)
                return 2
            # Recheck the original path after compilation so edits cannot receive stale acceptance.
            if hashlib.sha256(source_file.read_bytes()).hexdigest() != file_hash:
                RESULT.update(message="The source changed during compilation; check the current source again.", sourceChanged=True)
                return 2
            RESULT.update(success=True, formalVerification="KernelAccepted",
                          verificationScope="The exact formal statements, relative to explicit hypotheses and the reported foundational axioms.")
            return 0

        exit_code = 1
        try:
            exit_code = main()
        except subprocess.TimeoutExpired:
            RESULT.update(timedOut=True, message="Lean preparation or checking exceeded timeoutSeconds.")
            exit_code = 124
        except Exception as error:
            RESULT.update(error=type(error).__name__, message=str(error))
        RESULT["elapsedMilliseconds"] = round((time.monotonic()-STARTED)*1000)
        try:
            if PROOF_ROOT.is_dir() and not PROOF_ROOT.is_symlink():
                receipt_name = "receipt-" + uuid.uuid4().hex + ".json"
                RESULT["receipt"] = "lean/" + receipt_name
                atomic_write(PROOF_ROOT/receipt_name, json.dumps(RESULT, ensure_ascii=False, indent=2) + "\n")
        except OSError as receipt_error:
            RESULT.update(success=False, formalVerification="NotEstablished", receiptError=str(receipt_error))
            exit_code = 1
        # Keep the complete receipt on disk, including large compiler diagnostics.
        # The command bridge has a small response budget; do not let truncation erase
        # success/exit-code/audit fields or turn a compiler error into an unreadable blob.
        wire = dict(RESULT)
        wire.pop("sourceText", None)  # UI loads the immutable proof from the receipt, not the model context.
        for field in ("stdout", "stderr"):
            value = wire.get(field, "")
            if len(value) > 1800:
                wire[field] = value[:1800]
                wire[field + "Truncated"] = True
        if len(wire.get("diagnostics", [])) > 12:
            wire["diagnostics"] = wire["diagnostics"][:12]
            wire["diagnosticsTruncated"] = True
        if "diagnostics" in wire:
            wire["diagnostics"] = [line[:300] for line in wire["diagnostics"]]
        if len(wire.get("proofs", [])) > 8:
            wire["proofCount"] = len(wire["proofs"])
            wire["proofs"] = wire["proofs"][:8]
            wire["proofsTruncated"] = True
        if "proofs" in wire:
            wire["proofs"] = [{**proof, "declaration": proof["declaration"][:256], "statement": proof["statement"][:600],
                               "declarationTruncated": len(proof["declaration"]) > 256,
                               "statementTruncated": len(proof["statement"]) > 600} for proof in wire["proofs"]]
        if len(json.dumps(wire, ensure_ascii=True)) > 5000:
            wire.pop("stdout", None)
            wire.pop("stderr", None)
            wire["diagnostics"] = [line[:160] for line in wire.get("diagnostics", [])[:6]]
            if "proofs" in wire:
                wire["proofCount"] = len(RESULT["proofs"])
                wire["proofs"] = wire["proofs"][:3]
            wire["detailsInReceipt"] = True
        print(json.dumps(wire, ensure_ascii=True))
        sys.exit(exit_code)
        """";
}
