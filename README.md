# gesture-authentication

_Companion repository for the (double-blind) submission:_  
**Leveraging Biometric-Rich Hand Gestures for Head-Mounted Display Authentication.**  
**Anonymity notice:** This repository intentionally omits author names and affiliations for anonymous review.

---

## Overview
This Unity project implements a VR gesture capture and evaluation system for HMD (head-mounted display) authentication. It supports:
- **User-generated gesture creation**
- **Dictionary (predefined) gesture collection**
- **Attacks via guided/looped replay of reconstructed hand-joint motions**

Data and example gestures are provided in **Jackknife** format (see **Citing Jackknife**).

---

## Repository Layout
gesture-authentication/
├─ 2D/ # 10 two-dimensional gestures (Jackknife format)
├─ 3D/ # 10 three-dimensional gestures (Jackknife format)
├─ Tutorial/ # 4 tutorial gestures (Jackknife format)
├─ Assets/
│ ├─ Jackknife/ # Jackknife implementation (see citations below)
│ ├─ Materials/ Samples/ Sprites/ TextMesh Pro/ XR/ *.meta
│ ├─ Scenes/
│ │ ├─ Attack.unity # Attack study scene
│ │ ├─ Collection.unity # Dictionary gestures study scene
│ │ └─ Creation.unity # User-generated gestures study scene
│ └─ Scripts/
│ │├─ AttackManager.cs
│ │├─ CollectionManager.cs
│ │├─ CollisionDetection.cs
│ │├─ CreationManager.cs
│ │├─ ExperimentManager.cs
│ │└─ Handlers.cs
├─ Packages/ # Package lock manifest (dependencies)
├─ ProjectSettings/ # Unity project settings
├─ .gitignore
└─ .vsconfig


---

## Requirements
- **Unity**: _2022.3.57f1_ (project created for this exact LTS patch)
- **Platform**: _Windows_ (tested only)
- **Headsets**: _Meta Quest 2, Meta Quest 3_
- **GPU/CPU/RAM (tested)**: _RTX 4060, 13th Gen Intel i7, 16 GB RAM_
- **Run mode**: Debug (link cable; press **Play** in Unity Editor)

> **Note:** We only validated the Editor debug workflow. We did **not** ship an APK because rapid iteration and direct PC logging were required.

---

## Getting Started (Unity)

### 1) Open the project
1. Install **Unity 2022.3.57f1**.  
2. Open this repository folder in the Unity Hub and launch the Editor.

### 2) Install required packages
`Window → Package Manager` and install **exact** versions:
- **XR Core Utilities**: _2.5.1_
- **XR Plugin Management**: _4.5.1_
- **XR Hands**: _1.4.3_
- **XR Legacy Input Helpers**: _2.1.12_

### 3) Configure OpenXR
`Edit → Project Settings → XR Plug-in Management → OpenXR`:
- **Rendering Mode**: set to **Multi-Pass**
- **Profiles**: add **Hand Interaction Profile**
- **All Features**: tick **Hand Tracking Subsystem**

### 4) Run mode
- Use a link cable, wear the HMD, and **press Play** in the Unity Editor (Debug Mode).  
- Data are written directly to the PC (no device pull step).

---

## Scenes & Study Workflows

### Creation (User-Generated Gestures Study)
- **Open**: `Assets/Scenes/Creation.unity`
- **Select in Hierarchy**: `Experiment Manager` (root object)
- **Inspector parameters** (customizable):
  - **Subject Code**: ID or pseudonym of participant
  - **Continue Experiment** *(checkbox)*: resume from latest run for `Subject Code`
  - **Continue From Stage**: stage to resume (used when continuing)
  - **Enforce 3D Gesture** *(checkbox)*: enforce 3D via sphericity constraint
  - **Practice Rounds**
  - **Recall Attempts**
  - **Collect Rounds**
  - **Distract Time Seconds**
- **Output**: saved under `./Creation/Sub_<SubjectCode>/`  
  Each subject folder contains log files and a `gestures/` directory with all captured gestures.

---

### Collection (Dictionary Gestures Study)
- **Open**: `Assets/Scenes/Collection.unity`
- **Prepare data**:  
  In the **repository root**, create a folder named **`Gestures`**.  
  Place any Jackknife-formatted gestures you wish to collect there (commonly from `./2D/` or `./3D/`).
- **Select in Hierarchy**: `Experiment Manager`
- **Inspector parameters**:
  - **Subject Code**
  - **Continue Experiment**: _unused_ in Collection (inherited, no effect)
  - **N Blocks**: number of blocks
  - **M Gestures Per Type**: repetitions per gesture type within a block

---

### Attack (Attack Study)
- **Open**: `Assets/Scenes/Attack.unity`
- **Prepare data**:  
  In the **repository root**, create a folder named **`AttackGesture`**.  
  Copy in **Jackknife** gesture folders previously collected from **Creation** (user-generated) that you want to replay.
- **Select in Hierarchy**: `Experiment Manager`
- **Inspector parameters**:
  - **Subject Code**
  - **Continue Experiment** *(checkbox)* and **Continue From Stage**
  - **Fully Informed** *(checkbox)*: if checked, shows reconstructed joints from the gesture
    - When enabled, a **looped animation** of the joints plays **indefinitely**.  
      To stop the loop and proceed to practice, press **`D`** on the keyboard.
  - **Practice Rounds**
  - **Attack Rounds**

---

## Data & Formats
- **Jackknife format** is used for all bundled gestures:
  - `./2D/` — 10 two-dimensional gestures (Jackknife)
  - `./3D/` — 10 three-dimensional gestures (Jackknife)
  - `./Tutorial/` — 4 tutorial gestures (Jackknife)
- The project ships a **Jackknife** implementation under `Assets/Jackknife/` (see **Citing Jackknife** below).

---

## Scripts
Core logic is organized under `Assets/Scripts/`:
- **ExperimentManager.cs** — base class for all experiment managers
- **CreationManager.cs** — logic for user-generated gesture study
- **CollectionManager.cs** — logic for dictionary gestures study
- **AttackManager.cs** — logic for attack study
- **CollisionDetection.cs** — utilities for collision events
- **Handlers.cs** — input, user feedback, animation, interaction, and gesture subsystems

---

## Tips, Shortcuts & Troubleshooting
- **Reset HMD alignment / camera issues:** press **Space** to reset the scene to the participant’s current HMD position.
- **Attack loop control:** press **`D`** to stop the looped reconstructed-joints animation and continue.
- **No data recorded?** Confirm you are running in the Editor with the headset connected via cable (Debug Mode) and that the required XR packages are installed/enabled.
- **Hand tracking not available?** Re-check OpenXR configuration:
  - Multi-Pass rendering
  - Add **Hand Interaction Profile**
  - Enable **Hand Tracking Subsystem**
- **Dictionary/Attack data not found?** Ensure you created the root-level folders `Gestures/` and/or `AttackGesture/` and copied Jackknife gesture folders inside.

---

## Citing Jackknife
If you use the Jackknife implementation or format, please cite the original work:

    @inproceedings{taranta17jackknife,
     author = {Taranta II, Eugene M. and Samiei, Amirreza and Maghoumi, Mehran and Khaloo, Pooya and Pittman, Corey R. and LaViola Jr., Joseph J.},
     title = {Jackknife: A Reliable Recognizer with Few Samples and Many Modalities},
     booktitle = {Proceedings of the 2017 CHI Conference on Human Factors in Computing Systems},
     series = {CHI '17},
     year = {2017},
     location = {Denver, Colorado, USA},
     pages = {5850--5861},
     url = {http://doi.acm.org/10.1145/3025453.3026002},
     publisher = {ACM},
    }

Supplementary references:
- Repository: https://github.com/ISUE/Jackknife  

> The `Assets/Jackknife/` directory contains an implementation adapted for this project; please attribute the original authors accordingly.

---

## License
**TBD (post-review).**  
During anonymous review, this repository is provided for evaluation and research reproduction.

---
