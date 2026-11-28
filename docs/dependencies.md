# Dependencies

Dependencies are scanned for known vulnerabilities as part of delivery (NFR-SEC-10).

Two things do it. **The build** asks about every package when it restores them, and does not go on with a package that has a known vulnerability: so nothing is built, tested, or published with one, on any machine. **The scan of the delivery** runs at GitHub with every push to `main`, with every pull request, and once a day, because an advisory is published while nothing changes here and nobody builds. It writes what it found where the team works: in the summary of its run, and in an issue of the repository.

The rule of the build is `Directory.Build.props`, at the root. The scan is the workflow `.github/workflows/dependency-scan.yml`, with its three scripts in `.github/scripts`: `dependency-scan.sh` scans and writes the report, `dependency-scan-issue.sh` keeps the issue, and `dependency-scan-check.sh` holds the first and the build to a planted package.

## What is scanned

| Dependency | Scanned |
|---|---|
| The NuGet packages of the application, `src/Ecnanib`: those that the project references, and those that they bring | Yes: 38 packages when this was written |
| The NuGet packages of the tests, `tests/Ecnanib.Tests`, which are never deployed but run on the machines that build | Yes: 92 packages |
| Everything else that the application runs on or is built with | No: see "Not built yet" |

The application has no other code of a third party: `wwwroot` holds its own files alone.

| Rule | Detail |
|---|---|
| **What is known comes from the package source,** nuget.org. | It carries the advisories of the GitHub Advisory Database for the packages that it serves. Both the build and the scan ask it, with the SDK's own audit, so they know the same. |
| **Every package of the graph is asked about,** and not only those that a project names. | `NuGetAuditMode` is `all`. Most of the packages are brought by another. |
| **An advisory of any severity counts:** low, moderate, high, and critical. | `NuGetAuditLevel` is `low`. |

## The build

| Rule | How |
|---|---|
| **A package with a known vulnerability stops the restore,** and with it the build, the tests, and a publish. | `Directory.Build.props` makes errors of the four warnings of the audit: NU1901 for low, NU1902 for moderate, NU1903 for high, and NU1904 for critical. The error names the package, its version, and the advisory. |
| **It holds for both projects, and for a deployment that is built anywhere.** | The file is at the root, so every project under it takes it, whatever builds it. |
| **The audit is said in the file,** and is not left to a default. | `NuGetAudit`, `NuGetAuditMode`, and `NuGetAuditLevel` are written out, with the values that the SDK has by default today. |
| **A source that cannot be asked does not stop the build.** | NU1900, which says so, stays a warning: a machine without a network still builds. The scan of the delivery is what does not take that for clean. |

So the first that a developer sees of an advisory may be a build that fails:

```
error NU1903: Warning As Error: Package 'Newtonsoft.Json' 12.0.3 has a known high severity vulnerability, https://github.com/advisories/GHSA-5crp-9r3c-p9vr
```

What to do then is under "When the scan finds something".

## The scan of the delivery

The workflow "Dependency scan" runs:

| When | Why |
|---|---|
| With every push to `main` | What is delivered is a commit of `main`. |
| With every pull request | A package with a known vulnerability is seen before it reaches `main`. |
| Every day at 05:17 UTC | An advisory is published for a package that was fine yesterday. |
| When someone starts it, under "Actions" or with `gh workflow run dependency-scan.yml` | To scan now. |

One run, in order:

1. **It holds itself to a planted package:** see "Testing". A run whose scan would not find a package that is known to be vulnerable fails here, and says nothing about the packages of the application.
2. **It resolves the packages of `Ecnanib.slnx`,** with the audit of the restore switched off for this once. With it, a finding would stop the restore, and nothing would be left to report on.
3. **It asks the SDK for the packages of each project, and for those with a known vulnerability:** `dotnet package list --vulnerable --include-transitive`, answered as JSON.
4. **It reads the suppressions of each project that something was found in.** The SDK lists an advisory that a project suppresses, although the build lets it pass. The scan lists it apart, and does not count it.
5. **It writes the report** to the summary of the run, and for a scan of `main` it keeps the issue: see "Where the findings are reported".
6. **It fails the run** for a finding, and for a scan that did not happen.

The same scan runs on any machine with the SDK and `jq`:

```
bash .github/scripts/dependency-scan.sh Ecnanib.slnx
```

It writes the report to standard output, and ends with 0 when nothing was found, with 1 for a finding, and with 2 when the scan did not happen.

| Rule | Why |
|---|---|
| **A scan that did not happen is never taken for a clean one.** | The packages could not be restored, the SDK could not list them or reach the source, its answer names a problem, or a project lists no package at all: each ends the scan with 2, the run fails, and the report says which it was. |
| **What the SDK ends with is not read.** | `dotnet package list` ends with 0 whatever it found. The scan reads the answer. |
| **A project with no package fails the scan.** | Both projects have many. A project that lists none was not looked at, as far as the scan can tell. A project that is added with none of its own changes this rule. |
| **The actions of the workflow are named by commit.** | What runs with the token of the repository does not change when a tag is moved. The tag is in the comment beside each. |
| **The token reads the repository and writes issues, and nothing else.** | And a pull request from a fork gets a token that only reads. |
| **One scan of a branch runs at a time,** each to its end. | Two would write the issue at once. |

## Where the findings are reported

| Where | What | For which scan |
|---|---|---|
| **The summary of the run,** under "Actions" | The report: each package with a known vulnerability, with its version, its severity, the project, whether the project references it or another package brings it, and the advisory. Then what is suppressed, and what was scanned: each project with the number of its packages, the source that was asked, the commit, and the time. | Every scan, a clean one too |
| **The issue "Dependencies with known vulnerabilities",** with the label `dependency-scan` | The same report, with the run that found it and where this document says what to do. | A scan of `main` |
| **The run fails.** | GitHub tells whoever pushed the commit, and for the scan of each day whoever last changed the schedule of the workflow. | A scan with a finding, and a scan that did not happen |
| **The output of a build** | The error of the restore. | A build on any machine |

How the issue is kept:

| What the scan of `main` found | No issue is open | The issue is open |
|---|---|---|
| A finding | It opens the issue. | It writes the text again. It adds a comment when the findings are other than before, and none when they are the same, so a new advisory is told and the same one is not told again each day. |
| Nothing | It leaves the issues alone. | It closes the issue, with a comment that names the commit and the run. |
| The scan did not happen | It leaves the issues alone. | It leaves the issue as it is: such a scan says nothing about the packages. |

| Rule | Why |
|---|---|
| **There is one issue, and the scan finds it by its label.** | The label `dependency-scan` is the scan's own, and the scan makes it when it is not there. Do not give it to another issue. |
| **Closing the issue by hand makes no exception.** | The next scan of `main` that still finds something opens another. A suppression is the exception: see below. |
| **A pull request and a run of another branch write no issue.** | The issue says what `main` has. Their summary and their failed run say what they found. |

## When the scan finds something

1. **Read the advisory:** what it is about, and the version that has the fix.
2. **Take the version with the fix.**
   - `dotnet package update --vulnerable --project Ecnanib.slnx` does it for the packages that have one.
   - A package that a project references: its `Version` in the project file.
   - A package that another brings: `dotnet nuget why Ecnanib.slnx <package>` says which. Update that one. When it has no version yet that brings the fix, reference the fixed version of the package itself in the project file, with a comment that says why it is there, as `Ecnanib.csproj` does for `Microsoft.EntityFrameworkCore.Relational` for another reason.
3. **Run the tests, and the scan:** `dotnet test --solution Ecnanib.slnx`, and `bash .github/scripts/dependency-scan.sh Ecnanib.slnx`.
4. **Push.** The scan of the push closes the issue.

**When no version has the fix, and the advisory does not apply to how the application uses the package,** it is suppressed, in `Directory.Build.props`:

```xml
<ItemGroup>
  <!-- GHSA-xxxx-xxxx-xxxx: why it does not apply here. Followed in #123. -->
  <NuGetAuditSuppress Include="https://github.com/advisories/GHSA-xxxx-xxxx-xxxx" />
</ItemGroup>
```

| Rule | Why |
|---|---|
| **A suppression names one advisory, by its address, written as the scan writes it.** | It lets that advisory pass the build, for every project, and nothing else of the package. The scan takes it for suppressed only when the address is the same to the letter. |
| **It says why beside it, and names the issue that follows it.** | It is the only place where an exception is made, so it is where the reason is read. |
| **The scan goes on listing it,** under "Suppressed" in every report. | An exception that nobody sees is not taken out again. |
| **It is taken out when a version has the fix.** | Then the package is updated instead. |

An advisory that does apply, and has no fix, is not for a suppression. What is done about it is the team's to decide, in the issue.

## Before a release

The scan of the commit that is released has passed: `gh run list --workflow dependency-scan.yml --commit <commit>`, or the scan by hand, as above.

## Testing

```
bash .github/scripts/dependency-scan-check.sh
```

The check plants a package: it makes a project, in a temporary folder, that references `Newtonsoft.Json` 12.0.3, which has had a known vulnerability of high severity for years (GHSA-5crp-9r3c-p9vr). A copy of `Directory.Build.props` is beside the project, so the rule of the build is the repository's own. The repository itself never references the package.

| What it holds | How |
|---|---|
| **The build refuses the package.** | The restore of the project fails, with NU1903 and the address of the advisory. |
| **The scan finds it,** although the restore of the build fails. | The scan ends with 1. Its first line says that something was found, and its table names the package, the version, the severity, the project, and the advisory. |
| **An advisory that the project suppresses passes the build, and the scan lists it apart.** | With a `NuGetAuditSuppress` item for the advisory, the restore passes, and the scan ends with 0, says that it is clean, and names the advisory under "Suppressed". |
| **A scan that did not happen is not a clean one.** | The scan of a project that is not there ends with 2, and so does the scan of a project whose source cannot be reached. |

**It runs in every run of the workflow, before the scan,** so a scan that finds nothing says something. It asks the package source, so it is not among the tests of `dotnet test`, which reach no network.

When it fails because the advisory is no longer served, or the package is gone, another package with a known vulnerability takes its place in the script. One that the framework brings itself will not do: the SDK takes such a package out of the graph.

## Not built yet

- **The .NET runtime and the SDK,** on the machine that runs the application and on the one that builds it. Neither is a package. `dotnet sdk check` says whether what a machine has is current, and keeping it so is the deployment's.
- **PostgreSQL and its client tools:** the deployment's too.
- **The tool `dotnet-ef` of `dotnet-tools.json`, the images of `compose.yaml`, and the browser that Playwright downloads.** The audit of the SDK sees none of them. None is deployed.
- **The actions of the workflow.** They are named by commit, are updated by hand, and are not scanned.
- **Packages that are deprecated or out of date, with no known vulnerability:** `dotnet package list --deprecated` and `--outdated` say them.
- **Dependabot.** Its alerts and its updates are switched off for the repository.
- **A test of the script that keeps the issue.** It was tried against a stand-in for `gh` when it was written, and nothing holds it since.
- **The scan of each day, in a repository that nothing happens in.** GitHub switches the schedule of a workflow off after 60 days without activity in a public repository. It is switched on again under "Actions".
