using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

[UpdateInGroup(typeof(QlearningSystemGroup), OrderLast = true)]
public partial struct DrlTrainingSystem : ISystem
{
    private Random random;
    private int stepCounter;
    private float cumulativeReward;
    private float runningAverageReward;
    private const int TargetUpdateInterval = 500;
    private const int UpdateInterval = 100;

    public void OnCreate(ref SystemState state)
    {
        random = new Random(123);
        stepCounter = 0;
        state.RequireForUpdate<NeuralNetworkComponent>();
        state.RequireForUpdate<NeuralNetworkParametersComponent>();
        state.RequireForUpdate<TargetNeuralNetworkParametersComponent>();
        state.RequireForUpdate<DrlConfigComponent>();
          state.RequireForUpdate<GamePlayingTag>();
    }
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var replayBuffer = SystemAPI.GetSingletonBuffer<NeuralNetworkReplayBufferElement>();
        if (replayBuffer.Length < 32) return;

        var neuralNetworks = SystemAPI.GetSingleton<NeuralNetworkComponent>();
        var neuralNetworksParameters = SystemAPI.GetSingleton<NeuralNetworkParametersComponent>();
        var targetNetworkParameters = SystemAPI.GetSingleton<TargetNeuralNetworkParametersComponent>();
        var config = SystemAPI.GetSingleton<DrlConfigComponent>();

        var performanceMetricEntity  = SystemAPI.GetSingletonEntity<LossMetricComponent>();
        
        var miniBatchSize = math.min(replayBuffer.Length, 32);
        var miniBatch = new NativeArray<NeuralNetworkReplayBufferElement>(miniBatchSize, Allocator.TempJob);

        var indices = new NativeArray<int>(replayBuffer.Length, Allocator.Temp);
        // Increment step counter and update 
        stepCounter++;
        if (stepCounter % 30 == 0)
        {
           for (int i = 0; i < replayBuffer.Length; i++)
            {
                indices[i] = i;
            }

            for (int i = indices.Length - 1; i > 0; i--)
            {
                int j = random.NextInt(0, i + 1);
                int temp = indices[i];
                indices[i] = indices[j];
                indices[j] = temp;
            }

                int batchIndex = 0;
            for (int i = 0; i < replayBuffer.Length && batchIndex < miniBatchSize; i++)
            {
                if (replayBuffer[indices[i]].state.playerDistance <= 0.5)
                {
                    miniBatch[batchIndex] = replayBuffer[indices[i]];
                    batchIndex++;
                }
            }

            indices.Dispose();

            float totalLoss = 0;
            foreach (var experience in miniBatch)
            {
                NativeArray<float> qValues;
                NativeArray<float> hiddenLayerOutputs;
                NeuralNetworkUtility.ForwardPassWithIntermediate(neuralNetworks,
                    neuralNetworksParameters.inputWeights,
                    neuralNetworksParameters.hiddenWeights,
                    neuralNetworksParameters.hiddenBiases,
                    neuralNetworksParameters.outputBiases,
                    experience.state,
                    out hiddenLayerOutputs,
                    out qValues);
                NativeArray<float> nextQValues;
                NativeArray<float> discardedArray;
                NeuralNetworkUtility.ForwardPassWithIntermediate(neuralNetworks,
                    targetNetworkParameters.inputWeights,
                    targetNetworkParameters.hiddenWeights,
                    targetNetworkParameters.hiddenBiases,
                    targetNetworkParameters.outputBiases,
                    experience.nextState,
                    out discardedArray,
                    out nextQValues);
                

                float targetQValue = experience.reward + config.discountFactor * NeuralNetworkUtility.Max(nextQValues);

                var targets = new NativeArray<float>(qValues.Length, Allocator.Temp);
                qValues.CopyTo(targets);
                targets[experience.action] = targetQValue;

                var (outputGradients, hiddenGradients) = ComputeGradients(neuralNetworksParameters, hiddenLayerOutputs, qValues, targets);
                       
                UpdateNetworkParametersL2(ref neuralNetworks, ref neuralNetworksParameters, hiddenLayerOutputs, hiddenGradients, outputGradients, config.learningRate);
                // Calculate loss
                float loss = 0;
                for (int i = 0; i < qValues.Length; i++)
                {
                    loss += math.pow(qValues[i] - targets[i], 2);
                }
                totalLoss += loss/miniBatchSize;

                hiddenLayerOutputs.Dispose();
                discardedArray.Dispose();
                nextQValues.Dispose();
                qValues.Dispose();
                targets.Dispose();
                
            }

            miniBatch.Dispose();
            cumulativeReward += totalLoss;
            runningAverageReward = 0.99f * runningAverageReward + 0.01f * totalLoss;
            state.EntityManager.SetComponentData(performanceMetricEntity, new LossMetricComponent
            {
                totalLoss = math.round(totalLoss * 100f) / 100f
            });
            //UnityEngine.Debug.Log($"Calculated Loss: {totalLoss}");

        }
         
        // Update the performance metric component
        
        if (replayBuffer.Length >= 512)
        {
             // Log metrics
            //UnityEngine.Debug.Log($"Step: {stepCounter}, Cumulative Reward: {cumulativeReward}, Running Average Reward: {runningAverageReward}");
            UpdateTargetNetwork(ref state, neuralNetworksParameters, targetNetworkParameters);
            replayBuffer.Clear();
            stepCounter = 0;
        }
    }
    [BurstCompile]
    private (NativeArray<float> outputGradients, NativeArray<float> hiddenGradients) ComputeGradients(NeuralNetworkParametersComponent parameters, NativeArray<float> hiddenLayerOutputs, NativeArray<float> outputActivations, NativeArray<float> targets)
    {
        var outputGradients = new NativeArray<float>(outputActivations.Length, Allocator.Temp);
        var hiddenGradients = new NativeArray<float>(hiddenLayerOutputs.Length, Allocator.Temp);

        for (int i = 0; i < outputActivations.Length; i++)
        {
            outputGradients[i] = outputActivations[i] - targets[i];
        }

        for (int i = 0; i < hiddenLayerOutputs.Length; i++)
        {
            float error = 0;
            for (int j = 0; j < outputActivations.Length; j++)
            {
                error += outputGradients[j] * parameters.hiddenWeights[j * hiddenLayerOutputs.Length + i];
            }
            hiddenGradients[i] = error * hiddenLayerOutputs[i] * (1 - hiddenLayerOutputs[i]);
        }
        // Clip gradients
        float gradientClipValue = 0.5f; // Set this to the desired maximum gradient value
        for (int i = 0; i < outputGradients.Length; i++)
        {
            outputGradients[i] = math.clamp(outputGradients[i], -gradientClipValue, gradientClipValue);
        }
        for (int i = 0; i < hiddenGradients.Length; i++)
        {
            hiddenGradients[i] = math.clamp(hiddenGradients[i], -gradientClipValue, gradientClipValue);
        }
        return (outputGradients, hiddenGradients);
    }
    [BurstCompile]
    private void UpdateNetworkParameters(ref NeuralNetworkComponent neuralNetwork, ref NeuralNetworkParametersComponent parameters, NativeArray<float> hiddenLayerOutputs, NativeArray<float> hiddenGradients, NativeArray<float> outputGradients, float learningRate)
    {
        for (int i = 0; i < parameters.hiddenWeights.Length; i++)
        {
            int row = i / hiddenLayerOutputs.Length;
            int col = i % hiddenLayerOutputs.Length;
            parameters.hiddenWeights[i] -= learningRate * outputGradients[row] * hiddenLayerOutputs[col];
        }

        for (int i = 0; i < parameters.outputBiases.Length; i++)
        {
            parameters.outputBiases[i] -= learningRate * outputGradients[i];
        }

        for (int i = 0; i < parameters.inputWeights.Length; i++)
        {
            int row = i / neuralNetwork.inputSize;
            int col = i % neuralNetwork.inputSize;
            parameters.inputWeights[i] -= learningRate * hiddenGradients[row] * parameters.inputWeights[col];
        }

        for (int i = 0; i < parameters.hiddenBiases.Length; i++)
        {
            parameters.hiddenBiases[i] -= learningRate * hiddenGradients[i];
        }

        hiddenGradients.Dispose();
        outputGradients.Dispose();
    }
    [BurstCompile]
private void UpdateNetworkParametersL2(
    ref NeuralNetworkComponent neuralNetwork,
    ref NeuralNetworkParametersComponent parameters,
    NativeArray<float> hiddenLayerOutputs,
    NativeArray<float> hiddenGradients,
    NativeArray<float> outputGradients,
    float learningRate)
{
    float weightDecay = 0.0001f; // Regularization strength
    float gradientClipValue = 1.0f; // Maximum gradient value

    // Update input weights with weight decay and gradient clipping
    for (int i = 0; i < parameters.inputWeights.Length; i++)
    {
        int row = i / neuralNetwork.inputSize;
        int col = i % neuralNetwork.inputSize;

        float gradient = hiddenGradients[row] * parameters.inputWeights[col];
        gradient = math.clamp(gradient, -gradientClipValue, gradientClipValue);

        parameters.inputWeights[i] -= learningRate * (gradient + weightDecay * parameters.inputWeights[i]);
    }

    // Update hidden weights with weight decay and gradient clipping
    for (int i = 0; i < parameters.hiddenWeights.Length; i++)
    {
        int row = i / hiddenLayerOutputs.Length;
        int col = i % hiddenLayerOutputs.Length;

        float gradient = outputGradients[row] * hiddenLayerOutputs[col];
        gradient = math.clamp(gradient, -gradientClipValue, gradientClipValue);

        parameters.hiddenWeights[i] -= learningRate * (gradient + weightDecay * parameters.hiddenWeights[i]);
    }

    // Update biases
    for (int i = 0; i < parameters.outputBiases.Length; i++)
    {
        parameters.outputBiases[i] -= learningRate * outputGradients[i];
    }
    for (int i = 0; i < parameters.hiddenBiases.Length; i++)
    {
        parameters.hiddenBiases[i] -= learningRate * hiddenGradients[i];
    }

    hiddenGradients.Dispose();
    outputGradients.Dispose();
}

    [BurstCompile]
    private void UpdateTargetNetwork(ref SystemState state, NeuralNetworkParametersComponent sourceParameters, TargetNeuralNetworkParametersComponent targetParameters)
    {
        // // Dispose existing NativeArrays to prevent memory leaks
        // if (targetParameters.inputWeights.IsCreated) targetParameters.inputWeights.Dispose();
        // if (targetParameters.hiddenWeights.IsCreated) targetParameters.hiddenWeights.Dispose();
        // if (targetParameters.outputBiases.IsCreated) targetParameters.outputBiases.Dispose();
        // if (targetParameters.hiddenBiases.IsCreated) targetParameters.hiddenBiases.Dispose();

        // // Create new NativeArrays and copy data
        // targetParameters.inputWeights = new NativeArray<float>(sourceParameters.inputWeights.Length, Allocator.Persistent);
        // targetParameters.hiddenWeights = new NativeArray<float>(sourceParameters.hiddenWeights.Length, Allocator.Persistent);
        // targetParameters.outputBiases = new NativeArray<float>(sourceParameters.outputBiases.Length, Allocator.Persistent);
        // targetParameters.hiddenBiases = new NativeArray<float>(sourceParameters.hiddenBiases.Length, Allocator.Persistent);

        sourceParameters.inputWeights.CopyTo(targetParameters.inputWeights);
        sourceParameters.hiddenWeights.CopyTo(targetParameters.hiddenWeights);
        sourceParameters.outputBiases.CopyTo(targetParameters.outputBiases);
        sourceParameters.hiddenBiases.CopyTo(targetParameters.hiddenBiases);
    }
}
