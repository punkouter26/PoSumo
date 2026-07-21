# PoSumo — ML-Agents Training

2D biped/creature reinforcement-learning setup for the PoSumo Unity project
(Unity 6000.5.4f1, ML-Agents Release 23).

## Versions (matched pair)
- Unity package: `com.unity.ml-agents` **4.0.0** (installed from local `release_23` source at `Training/ml-agents/com.unity.ml-agents`)
- Python package: `mlagents` **1.2.0.dev0** (installed from `release_23` source into `Training/venv`)
- Python: 3.10.11 (required range: >=3.10.1, <=3.10.12)

## Layout
```
Training/
  venv/            Python virtual-env with mlagents installed
  ml-agents/       release_23 source clone (Unity package is referenced from here)
  configs/         PPO training configs (PoSumoBiped.yaml)
  results/         mlagents-learn output (checkpoints, .onnx models, tensorboard)
```

## Train
```powershell
# from the project root (c:\Users\punko\Downloads\PoSumo)
Training\venv\Scripts\Activate.ps1
mlagents-learn Training\configs\PoSumoBiped.yaml --run-id=posumo01 --results-dir=Training\results
# When the console prints "Start training by pressing Play", press Play in the Unity Editor.
```
Resume an interrupted run by adding `--resume`; overwrite it with `--force`.

## Watch training
```powershell
Training\venv\Scripts\Activate.ps1
tensorboard --logdir Training\results
```

## After training
`mlagents-learn` writes `Training/results/posumo01/PoSumoBiped.onnx`.
Copy it into `Assets/_PoSumo/Models/` and assign it to the agent's
BehaviorParameters → Model field, then set Behavior Type to "Inference Only"
to watch the trained policy without Python.
