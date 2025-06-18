using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;

internal class CollectionManager : ExperimentManager
{
    #region Enums
    private enum ExperimentStage
    {
        Setup,
        Collect,
        Finish
    }
    #endregion

    #region Fields
    private ExperimentStage currentStage;
    string queryDir;
    private List<List<string>> blocks;
    private int currentBlockIndex, currentGestureIndex;
    private TrailHandler tutorialTrail;
    #endregion

    #region Serialized Fields
    [SerializeField] private int nBlocks;
    [SerializeField] private int mGesturesPerType;
    #endregion

    #region Unity Methods
    protected override void Start()
    {
        base.Start();

        tutorialTrail = new TrailHandler(tutorialLine);
    }

    protected void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
            ResetCoordinateFrame();
    }
    #endregion

    #region Initialization
    protected override string GetExperimentType() => "Collection";

    protected override void SetupNewExperiment()
    {
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        logPath = Path.Combine(experimentPath, $"{timestamp}_log.txt");
        experimentPath = Path.Combine(experimentPath, "gestures", timestamp);
        gestureHandler = new GestureHandler(experimentPath, mainCameraTransform, interactionRegion.transform, new TrailHandler(gestureTrail), new TrailHandler(xLine),
            new TrailHandler(yLine), new TrailHandler(zLine), leftMark, rightMark, topMark, baseMark, frontMark, feedbackSphere);
        
        if (!Directory.Exists(experimentPath))
            Directory.CreateDirectory(experimentPath);

        string gesturesPath = Path.Combine(Directory.GetParent(Application.dataPath).ToString(), "Gestures");
        List<string> gestureSubdirs = Directory.GetDirectories(gesturesPath).Select(Path.GetFileName).ToList();
        gestureSubdirs.Sort();

        blocks = new List<List<string>>();
        for (int i = 0; i < nBlocks; i++)
        {
            List<string> block = new List<string>();
            foreach (var gesture in gestureSubdirs)
                for (int j = 0; j < mGesturesPerType; j++)
                    block.Add(gesture);
            block = block.OrderBy(x => UnityEngine.Random.value).ToList();
            blocks.Add(block);
        }

        foreach (var subdir in gestureSubdirs)
        {
            string sampleDir = Path.Combine(experimentPath, subdir);
            Directory.CreateDirectory(sampleDir);
            File.Copy(Path.Combine(gesturesPath, subdir, "ex.txt"), Path.Combine(sampleDir, "ex.txt"));
        }

        currentStage = ExperimentStage.Setup;

        TransitionToSetup();
    }

    protected override void LoadPreviousExperiment()
    {
        throw new NotImplementedException("LoadPreviousExperiment not implemented.");
    }
    #endregion

    #region Stage Transitions
    protected override void TransitionToNextStage()
    {
        TransitionToCollect();
    }

    private void TransitionToCollect()
    {
        currentStage = ExperimentStage.Collect;
        
        SetupCollectUI();
        SetupCollectStage();
    }

    private void TransitionToFinish()
    {
        currentStage = ExperimentStage.Finish;
        
        SetupFinishUI();
    }
    #endregion

    #region Stage Helpers
    private void SetupCollectStage()
    {
        if (currentBlockIndex >= blocks.Count)
        {
            TransitionToFinish();

            return;
        }

        queryDir = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{blocks[currentBlockIndex][currentGestureIndex]}_block{currentBlockIndex}";
        Directory.CreateDirectory(Path.Combine(experimentPath, queryDir));
        
        SetupCurrentGesture(); 
        PositionObject(informationBoard, localCoordinateFrame.transform, BoardPositionOffset, Vector3.zero, Quaternion.identity, BoardDistance);
        PositionObject(interactionRegion, localCoordinateFrame.transform, InteractionRegionPositionOffset, InteractionRegionPositionOffset, Quaternion.identity, InteractionRegionDistance);

        Log($"Block {currentBlockIndex + 1}, Index {currentGestureIndex + 1}, {blocks[currentBlockIndex][currentGestureIndex]}", currentStage.ToString());

        ConfigureGestureCallbacks(() =>
        {
            infoTextCenterBottom.text = "<color=red>The gesture doesn't match! Try again.</color>";

            TransitionToCollect();
        }, () =>
        {

            UpdateGestureIndex();

            infoTextCenterBottom.text = "<color=green>The gesture matches!</color>";

            TransitionToCollect();
        });
        
        InitializeHandSubsystem(gestureHandler.OnUpdatedHandsRecord);
    }
    
    private void SetupCollectUI()
    {
        SetInformationUI(centerTop: true, centerBottom: true, picture: true);
        interactionRegion.GetComponent<LineRenderer>().enabled = true;
        infoTextCenterTop.text = $"Block {currentBlockIndex}/{nBlocks} | Collect {currentGestureIndex}/{blocks[0].Count}";
        infoPicture.sprite = rightHandPinchSprite;

        gestureHandler.Clear();
        tutorialTrail.Clear();
        EnableTutorialElements(true);
    }
    private void SetupFinishUI()
    {
        SetInformationUI(center: true);
        infoTextCenter.text = "The experiment is over. Thank you for participation!";
        
        gestureHandler.Clear();
        tutorialTrail.Clear();
    }
    #endregion

    #region Helpers
    private void ConfigureGestureCallbacks(Action onFail, Action onPass = null)
    {
        gestureHandler.OnGestureFailed = onFail;
        gestureHandler.OnGesturePassed = onPass;
        gestureHandler.OnGestureStarted = () =>
        {
            Log("Started drawing a gesture", currentStage.ToString());
        };
        gestureHandler.OnGestureFinished = () =>
        {
            Log("Finished drawing a gesture", currentStage.ToString());

            SaveQueryRecord();
        };
    }

    private void UpdateGestureIndex()
    {
        currentGestureIndex++;

        if (currentGestureIndex >= blocks[currentBlockIndex].Count)
        {
            currentBlockIndex++;
            currentGestureIndex = 0;
        }
    }

    private void SetupCurrentGesture()
    {
        var points = LoadGesturePoints("Gestures", blocks[currentBlockIndex][currentGestureIndex]);

        startPoint.transform.localPosition = points[0];
        endPoint.transform.localPosition = points[points.Count - 1];
        for (int i = 0; i < points.Count; i++)
        {
            tutorialTrail.DrawPoint(points[i]);
        }
    }

    private void SaveQueryRecord()
    {
        gestureHandler.Save(queryDir);

        double matchScore = gestureHandler.JackknifeScore(blocks[currentBlockIndex][currentGestureIndex], queryDir);

        Log($"Gesture match score: {matchScore:0.#######}", currentStage.ToString());

        bool isMatch = matchScore >= GestureScoreThreshold;

        Log($"Draw Time ({(isMatch ? "Passed" : "Failed")}): {gestureHandler.DrawTimeMs}ms", currentStage.ToString());

        if (isMatch) gestureHandler.OnGesturePassed?.Invoke();
        else gestureHandler.OnGestureFailed?.Invoke();
    }
    #endregion
}