using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Integrations.Match3;

public class LSD : CogsAgent
{
    // ------------------BASIC MONOBEHAVIOR FUNCTIONS-------------------

    // Initialize values
    protected override void Start()
    {
        base.Start();
        AssignBasicRewards();
    }

    // For actual actions in the environment (e.g. movement, shoot laser)
    // that is done continuously
    protected override void FixedUpdate()
    {
        base.FixedUpdate();

        LaserControl();
        // Movement based on DirToGo and RotateDir
        moveAgent(dirToGo, rotateDir);
    }


    // --------------------AGENT FUNCTIONS-------------------------

    // Get relevant information from the environment to effectively learn behavior
    public override void CollectObservations(VectorSensor sensor)
    {
        // Agent velocity in x and z axis
        var localVelocity = transform.InverseTransformDirection(rBody.velocity);
        sensor.AddObservation(localVelocity.x);
        sensor.AddObservation(localVelocity.z);

        // Time remaining and frozen time
        sensor.AddObservation(timer.GetComponent<Timer>().GetTimeRemaning());
        sensor.AddObservation(GetFrozenTime());

        // Agent's current rotation
        var localRotation = transform.rotation;
        sensor.AddObservation(localRotation.y);

        // Agent, home base, and enemy's position
        var agentPosition = this.transform.localPosition;
        var enemyPosition = enemy.transform.localPosition;
        
        sensor.AddObservation(agentPosition);
        sensor.AddObservation(baseLocation.localPosition);
        // In testing both positions are being given, filter the actual enemy. Might be unnecessary during actual play
        if (!enemy.transform.localPosition.Equals(agentPosition)) sensor.AddObservation(enemyPosition);
        
        // Distance from agent to enemy (for the laser range)
        var enemyToAgentDistance = Vector3.Distance(agentPosition, enemyPosition);
        sensor.AddObservation(enemyToAgentDistance);
        
        // for each target in the environment, add: its position, whether it is being carried,
        // and whether it is in a base
        foreach (GameObject target in targets)
        {
            sensor.AddObservation(target.transform.localPosition);
            sensor.AddObservation(target.GetComponent<Target>().GetCarried());
            sensor.AddObservation(target.GetComponent<Target>().GetInBase());
        }

        // Whether the agent is frozen
        sensor.AddObservation(IsFrozen());
    }

    // For manual override of controls. This function will use keyboard presses to simulate output from your NN
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discreteActionsOut = actionsOut.DiscreteActions;
        discreteActionsOut[0] = 0; //Simulated NN output 0
        discreteActionsOut[1] = 0; //....................1
        discreteActionsOut[2] = 0; //....................2
        discreteActionsOut[3] = 0; //....................3
        discreteActionsOut[4] = 0; //....................4

        if (Input.GetKey(KeyCode.UpArrow))    discreteActionsOut[0] = 1;
        if (Input.GetKey(KeyCode.DownArrow))  discreteActionsOut[0] = 2;
        if (Input.GetKey(KeyCode.RightArrow)) discreteActionsOut[1] = 1;
        if (Input.GetKey(KeyCode.LeftArrow))  discreteActionsOut[1] = 2;
        if (Input.GetKey(KeyCode.Space))      discreteActionsOut[2] = 1; //Shoot
        if (Input.GetKey(KeyCode.A))          discreteActionsOut[3] = 1; //GoToNearestTarget
        if (Input.GetKey(KeyCode.S))          discreteActionsOut[4] = 1;
    }

    // What to do when an action is received (i.e. when the Brain gives the agent information about possible actions)
    public override void OnActionReceived(ActionBuffers actions)
    {
        int forwardAxis = (int)actions.DiscreteActions[0]; //NN output 0
        int rotateAxis = (int)actions.DiscreteActions[1];
        int shootAxis = (int)actions.DiscreteActions[2];
        int goToTargetAxis = (int)actions.DiscreteActions[3];
        int goToBaseAxis = (int)actions.DiscreteActions[4];
        
        AddReward(-0.0001f);
        if (IsFrozen()) AddReward(rewardDict["frozen"]);
        if (enemy.GetComponent<CogsAgent>().IsFrozen()) AddReward(rewardDict["enemy-frozen"]);

        MovePlayer(forwardAxis, rotateAxis, shootAxis, goToTargetAxis, goToBaseAxis);
    }


// ----------------------ONTRIGGER AND ONCOLLISION FUNCTIONS------------------------
    // Called when object collides with or trigger (similar to collide but without physics) other objects
    protected override void OnTriggerEnter(Collider collision)
    {
        if (collision.gameObject.CompareTag("HomeBase") &&
            collision.gameObject.GetComponent<HomeBase>().team == GetTeam())
        {
            if (GetCarrying() == 0)
            {
                AddReward(rewardDict["in-base-for-nothing"]);
            }
            else
            {
                AddReward(rewardDict["dropped-one-target"] * GetCarrying());
            }
        } 

        base.OnTriggerEnter(collision);
    }

    protected override void OnCollisionEnter(Collision collision)
    {
        //target is not in my base and is not being carried and I am not frozen
        if (collision.gameObject.CompareTag("Target") &&
            collision.gameObject.GetComponent<Target>().GetInBase() != GetTeam() &&
            collision.gameObject.GetComponent<Target>().GetCarried() == 0 && !IsFrozen())
        {
            AddReward(rewardDict["captured-target"]);
        }

        if (collision.gameObject.CompareTag("Wall"))
        {
            AddReward(rewardDict["hit-wall"]);
        }

        base.OnCollisionEnter(collision);
    }


    //  --------------------------HELPERS----------------------------
    private void AssignBasicRewards()
    {
        // TODO fix these
        rewardDict = new Dictionary<string, float>();

        rewardDict.Add("frozen", -1f); 
        rewardDict.Add("shooting-laser", 0f); // unused
        rewardDict.Add("hit-enemy", 0.5f); // unused
        rewardDict.Add("dropped-one-target", 1f); 
        rewardDict.Add("dropped-targets", 0f); // unused
        rewardDict.Add("hit-wall", -0.9f);
        rewardDict.Add("captured-target", 0.5f);
        rewardDict.Add("in-base-for-nothing", -0.5f);
        rewardDict.Add("enemy-frozen", 0.1f);
    }

    private void MovePlayer(int forwardAxis, int rotateAxis, int shootAxis, int goToTargetAxis, int goToBaseAxis)
    {
        dirToGo = Vector3.zero;
        rotateDir = Vector3.zero;

        Vector3 forward = transform.forward;
        Vector3 backward = -transform.forward;
        Vector3 right = transform.up;
        Vector3 left = -transform.up;

        //forwardAxis:
        // 0 -> do nothing. This case is not necessary to include, it's only here to explicitly show what happens in case 0
        // 1 -> go forward
        // 2 -> go backward
        switch (forwardAxis)
        {
            case 0:
                break;
            case 1:
                dirToGo = forward;
                break;
            case 2:
                dirToGo = backward;
                break;
        }

        //rotateAxis:
        // 0 -> do nothing
        // 1 -> go right
        // 2 -> go left
        switch (rotateAxis)
        {
            case 0:
                break;
            case 1:
                rotateDir = right;
                break;
            default:
                rotateDir = left;
                break;
        }
        
        
        //shoot (equivalent to if else statement that we had)
        SetLaser(shootAxis == 1);

        //go to the nearest target
        if (goToTargetAxis == 1) 
        {
            GoToNearestTarget();
        }

        if (goToBaseAxis == 1) 
        {
            GoToBase();
        }
    }

    // Go to home base
    private void GoToBase()
    {
        TurnAndGo(GetYAngle(myBase));
    }

    // Go to the nearest target
    private void GoToNearestTarget()
    {
        GameObject target = GetNearestTarget();
        if (target != null)
        {
            float rotation = GetYAngle(target);
            TurnAndGo(rotation);
        }
    }

    // Rotate and go in specified direction
    private void TurnAndGo(float rotation)
    {
        if (rotation < -5f)
        {
            rotateDir = transform.up;
        }
        else if (rotation > 5f)
        {
            rotateDir = -transform.up;
        }
        else
        {
            dirToGo = transform.forward;
        }
    }

    // return reference to nearest target
    protected GameObject GetNearestTarget()
    {
        float distance = 200;
        GameObject nearestTarget = null;
        foreach (var target in targets)
        {
            float currentDistance = Vector3.Distance(target.transform.localPosition, transform.localPosition);
            if (currentDistance < distance && target.GetComponent<Target>().GetCarried() == 0 &&
                target.GetComponent<Target>().GetInBase() != team)
            {
                distance = currentDistance;
                nearestTarget = target;
            }
        }

        return nearestTarget;
    }

    private float GetYAngle(GameObject target)
    {
        Vector3 targetDir = target.transform.position - transform.position;
        Vector3 forward = transform.forward;

        float angle = Vector3.SignedAngle(targetDir, forward, Vector3.up);
        return angle;
    }
}