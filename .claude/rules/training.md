# Training Rules

## TensorBoard runs whenever a trainer runs — no exceptions

**Never start an ML-Agents run without TensorBoard alongside it.** A run with no
TensorBoard is a run you cannot judge: the only accept/reject signal this project
recognises for a self-play fight run is the **shape of the ELO curve**, and ELO exists
only as a TensorBoard scalar. Mean reward — the one number the console prints — is
explicitly *not* the criterion, because it moves in the opposite direction when a policy
learns to farm shaping instead of winning bouts (measured: reward climbed to ~36 while
ELO fell 1198 → 1140).

So a run without TensorBoard does not merely lack a nice graph. It produces hours of
compute whose only readable outcome is the number you must not use.

### How to satisfy this rule

**Use a wrapper. They already do it, and that is the whole reason they exist.**

| Script | TensorBoard |
|---|---|
| `Training/Start-Training.ps1` | starts it **before** the trainer, so the run appears as soon as the first summary lands |
| `Training/Start-StaminaExtension.ps1` | starts it, and skips if something already holds 6006 |
| `Training/Run-GaitCampaign.ps1` | inherits it — it shells out to `Start-StaminaExtension.ps1` per batch |

**The failure mode this rule exists to prevent is calling `mlagents-learn.exe`
directly.** That is the one path with no TensorBoard in it, and nothing about the run
looks wrong while it happens — the trainer prints steps, the env players spin up, and
the absence is only discovered when someone asks how the run went.

If a raw invocation is genuinely necessary, start TensorBoard first and say why in the
same breath:

```powershell
Training\venv\Scripts\python.exe -m tensorboard.main `
  --logdir Training/results --port 6006 --reload_interval 15
```

### Two things about port 6006 that have each cost a run

- **A second bind fails quietly.** Launch a second TensorBoard while one is live and it
  does not error loudly — you notice an hour later with no graphs. `Start-StaminaExtension.ps1`
  checks `Get-NetTCPConnection -LocalPort 6006` first and leaves a live one alone; match
  that behaviour rather than starting one blind.
- **Kill TensorBoard BEFORE any `--force` run, and restart it after.** It holds Windows
  handles on the run directories, so a `--force` fired while it is live leaves the old
  contents *in place*, silently. That is not cosmetic: the surviving checkpoints outrank
  the new run's numerically for a long while, so `DeployBrain.DeployLatestCheckpoint`
  will ship a brain from the run you thought you deleted.

### Judging the run

TensorBoard answers "how did this run go". The HTTP telemetry endpoint
(`curl http://127.0.0.1:8787/metrics`) answers "what is this env doing right now", which
is the question you actually have when a headless run has gone quiet. They are not
substitutes for each other — the endpoint has no history and TensorBoard has no live
view.

Accept a fight run on the **shape** of the ELO curve: a monotonic slide is regression
(reject); oscillation within a point or two of the start is noise and is fine, because
flat ELO against a pool that is itself retraining means the policy kept pace.
