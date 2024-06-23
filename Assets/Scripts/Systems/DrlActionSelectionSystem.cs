using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Unity.Collections;
using Unity.Burst;
using Random = Unity.Mathematics.Random;

[UpdateAfter(typeof(DrlEnemyStateSystem))]
[UpdateInGroup(typeof(QlearningSystemGroup))]
public partial struct DrlActionSelectionSystem : ISystem
{
    private Random random;
    public void OnCreate(ref SystemState state)
    {
        random = new Random(123);
       
        state.RequireForUpdate<EnemyActionComponent>();
        state.RequireForUpdate<NeuralNetworkComponent>();
        state.RequireForUpdate<NeuralNetworkParametersComponent>();
        state.RequireForUpdate<DrlConfigComponent>();
    }
    
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var neuralNetworks = SystemAPI.GetSingleton<NeuralNetworkComponent>();
        var neuralNetworksEntity = SystemAPI.GetSingletonEntity<NeuralNetworkComponent>();
        var neuralNetworksParameters = SystemAPI.GetSingleton<NeuralNetworkParametersComponent>();
        var DrlReplayBuffer = SystemAPI.GetBuffer<NeuralNetworkReplayBufferElement>(neuralNetworksEntity);
        var DrlConfig = SystemAPI.GetSingleton<DrlConfigComponent>();
        
        var randomCopy = random;
        
        var job = new DrlActionSelectionJob
        {
            NeuralNetworks = neuralNetworks,
            NeuralNetworksParameters = neuralNetworksParameters,
            DrlReplayBuffer = DrlReplayBuffer,
            Random = randomCopy
        };

        state.Dependency = job.Schedule(state.Dependency);
    }

    [BurstCompile]
    private partial struct DrlActionSelectionJob : IJobEntity
    {
        public NeuralNetworkComponent NeuralNetworks;
        public NeuralNetworkParametersComponent NeuralNetworksParameters;
        public DynamicBuffer<NeuralNetworkReplayBufferElement> DrlReplayBuffer;
        public Random Random;

        public void Execute(
            ref EnemyActionComponent enemy,
            ref EnemyActionTimerComponent enemyActiontimer,
            ref EnemyEpsilonComponent enemyEpsilon,
            in EnemyPossibleActionComponent possibleActions,
            ref EnemyStateComponent enemyState,
            in EnemyPreviousStateComponent previousState,
            ref EnemyRewardComponent enemyReward,
            ref EnemyActionsCooldownComponent enemyActionsCooldownComponent)
        {
            if (!enemy.isDoingAction)
            {
                NativeArray<float> actionsQvalues;
                NeuralNetworkUtility.ForwardPass(NeuralNetworks, NeuralNetworksParameters, enemyState, out actionsQvalues);
                var (chosenAction, actionValue) = SelectAction(enemyEpsilon.epsilon, actionsQvalues, possibleActions, enemyActionsCooldownComponent);

                DrlReplayBuffer.Add(new NeuralNetworkReplayBufferElement
                {
                    state = previousState.previousState,
                    action = chosenAction,
                    reward = enemyReward.earnReward,
                    nextState = enemyState,
                    done = false
                });
           
                enemyReward.earnReward = 0;
                
                enemy.chosenAction = chosenAction;
                enemy.numberOfSteps = math.min(enemy.numberOfSteps + 1, 100);
                enemyEpsilon.epsilon = math.max(enemyEpsilon.epsilon - 0.001f, 0.01f);// Linear epsilon decay
                enemy.isDoingAction = true;

                enemyActiontimer.actionDuration = 1f; // Adjust based on chosen action
            }
        }

        private (int action, float value) SelectAction(float epsilon, NativeArray<float> actionsQvalues, EnemyPossibleActionComponent possibleActions,EnemyActionsCooldownComponent enemyActionsCooldown)
        {
            NativeList<int> validActions = new NativeList<int>(Allocator.Temp);

            for (int i = 0; i <= 8; i++)
            {
                if (IsActionPossible(possibleActions , enemyActionsCooldown , i))
                {
                    validActions.Add(i);
                }
            }

            int chosenAction;
            float actionValue;

            if (Random.NextFloat(0f, 1f) < epsilon || validActions.Length == 0)
            {
                int randomIndex = Random.NextInt(0, validActions.Length);
                chosenAction = validActions[randomIndex];
                actionValue = actionsQvalues[chosenAction];
            }
            else
            {
                (chosenAction, actionValue) = GetMaxValueActionFromValidActions(actionsQvalues, validActions);
            }

            validActions.Dispose();

            return (chosenAction, actionValue);
        }

        private bool IsActionPossible(EnemyPossibleActionComponent possibleActions,EnemyActionsCooldownComponent enemyActionsCooldown, int action)
        {
            switch ((DrlActionSelectionSystem.EnemyActionEnum)action)
            {
                case DrlActionSelectionSystem.EnemyActionEnum.foward:
                    return possibleActions.canForward;
                case DrlActionSelectionSystem.EnemyActionEnum.backward:
                    return possibleActions.canBackward;
                case DrlActionSelectionSystem.EnemyActionEnum.stepRight:
                    return possibleActions.canStepRight;
                case DrlActionSelectionSystem.EnemyActionEnum.stepLeft:
                    return possibleActions.canStepLeft;
                case DrlActionSelectionSystem.EnemyActionEnum.dash:
                    return possibleActions.canDash && enemyActionsCooldown.cooldownDashTimer <= 0;
                case DrlActionSelectionSystem.EnemyActionEnum.block:
                    return possibleActions.canBlock && enemyActionsCooldown.cooldownBlockTimer <= 0;
                case DrlActionSelectionSystem.EnemyActionEnum.heal:
                    return possibleActions.canHeal && enemyActionsCooldown.cooldownHealTimer <= 0;
                case DrlActionSelectionSystem.EnemyActionEnum.jump:
                    return possibleActions.canJump && enemyActionsCooldown.cooldownJumpTimer <= 0;
                case DrlActionSelectionSystem.EnemyActionEnum.stay:
                    return possibleActions.canStay && enemyActionsCooldown.cooldownStayTimer <= 0;
                default:
                    return false;
            }
        }

        private (int index, float value) GetMaxValueActionFromValidActions(NativeArray<float> qvalues, NativeList<int> validActions)
        {
            float maxActionValue = float.MinValue;
            int maxActionIndex = 0;

            for (int i = 0; i < validActions.Length; i++)
            {
                if (qvalues[validActions[i]] > maxActionValue)
                {
                    maxActionValue = qvalues[validActions[i]];
                    maxActionIndex = validActions[i];
                }
            }

            return (maxActionIndex, maxActionValue);
        }
    }

    public enum EnemyActionEnum
    {
        foward,
        backward,
        stepRight,
        stepLeft,
        dash,
        block,
        heal,
        jump,
        stay
    }
}