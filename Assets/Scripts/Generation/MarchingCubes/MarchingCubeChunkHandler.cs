﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace MarchingCubes
{
    public class MarchingCubeChunkHandler : MonoBehaviour, IMarchingCubeChunkHandler
    {

        protected int kernelId;

        protected const int threadGroupSize = 8;

        public const int ChunkSize = 50;

        public const int CHUNK_VOLUME = ChunkSize * ChunkSize * ChunkSize;

        public GameObject chunkPrefab;

        public GameObject threadedChunkPrefab;

        public const int PointsPerChunkAxis = ChunkSize + 1;

        public Dictionary<Vector3Int, IMarchingCubeChunk> chunks = new Dictionary<Vector3Int, IMarchingCubeChunk>();

        [Range(1, 253)]
        public int blockAroundPlayer = 16;

        private const int maxTrianglesLeft = 3000000;

        public ComputeShader marshShader;

        [Header("Voxel Settings")]
        //public float boundsSize = 8;
        public Vector3 noiseOffset = Vector3.zero;

        //[Range(2, 100)]
        //public int numPointsPerAxis = 30;


        protected int NeededChunkAmount
        {
            get
            {
                int amount = Mathf.CeilToInt(blockAroundPlayer / PointsPerChunkAxis);
                if (amount % 2 == 1)
                {
                    amount += 1;
                }
                return amount;
            }
        }

        //public PlanetMarchingCubeNoise noiseFilter;

        //public TerrainNoise terrainNoise;

        public BaseDensityGenerator densityGenerator;

        public bool useTerrainNoise;


        public int deactivateAfterDistance = 40;

        protected int DeactivatedChunkDistance => Mathf.CeilToInt(deactivateAfterDistance / PointsPerChunkAxis);

        public Material chunkMaterial;

        [Range(0, 1)]
        public float surfaceLevel = 0.45f;

        public Transform player;

        public int buildAroundDistance = 2;

        DateTime start;
        DateTime end;

        private void Start()
        {
            start = DateTime.Now;
            //Debug.Log("Max threadpool threads:" + ThreadPool.thread());
            CreateBuffersIfNeeded();
            kernelId = marshShader.FindKernel("March");
            IMarchingCubeChunk chunk = FindNonEmptyChunkAround(player.position);
            startPos = chunk.ChunkOffset * ChunkSize;
            maxChunkSqrDistance = buildAroundDistance * buildAroundDistance;

            StartCoroutine(BuildRelevantChunksParallelAround(chunk));
            ReleaseBuffersIfNeeded();
        }

        private void Update()
        {
            //CheckChunksAround(player.position);
        }

        protected IEnumerator UpdateChunks()
        {
            yield return null;


            yield return new WaitForSeconds(3);

            yield return UpdateChunks();
        }


        public void BuildRelevantChunksAround(IMarchingCubeChunk chunk)
        {
            if (chunk.NeighbourCount <= 0)
                return;
            do
            {
                foreach (Vector3Int v3 in chunk.NeighbourIndices)
                {
                    if (!Chunks.ContainsKey(v3) && (startPos - v3 * ChunkSize).sqrMagnitude < maxChunkSqrDistance)
                    {
                        IMarchingCubeChunk newChunk = CreateChunkAt(v3);
                        foreach (Vector3Int newV3 in newChunk.NeighbourIndices)
                        {
                            if (!Chunks.ContainsKey(newV3))
                            {
                                neighbours.Enqueue(newV3);
                            }
                        }
                    }
                }
                chunk = null;
                if (neighbours.Count > 0)
                {
                    Vector3Int next;
                    bool hasNext = false;
                    do
                    {
                        next = neighbours.Dequeue();
                        hasNext = !Chunks.ContainsKey(next);
                    } while (!hasNext && neighbours.Count > 0);

                    if (hasNext)
                    {
                        chunk = CreateChunkAt(next);
                    }
                }
            } while (chunk != null && totalTriBuild < maxTrianglesLeft);
            end = DateTime.Now;
            Debug.Log("Total millis: " + (end - start).TotalMilliseconds);
            if (totalTriBuild >= maxTrianglesLeft)
            {
                Debug.Log("Aborted");
            }
            Debug.Log("Total triangles: " + totalTriBuild);

            Debug.Log($"Number of chunks: {Chunks.Count}");
        }

        protected Vector3Int startPos;
        protected float maxChunkSqrDistance;
        protected Queue<Vector3Int> neighbours = new Queue<Vector3Int>();

        protected SortedDictionary<float, List<Vector3Int>> sortedNeighbourds = new SortedDictionary<float, List<Vector3Int>>();

        protected void AddSortedNeighbour(float key, Vector3Int v)
        {
            List<Vector3Int> l;
            if (!sortedNeighbourds.TryGetValue(key, out l))
            {
                l = new List<Vector3Int>();
                sortedNeighbourds[key] = l;
            }
            l.Add(v);
        }

        protected Vector3Int RemoveFirst()
        {
            List<Vector3Int> l = sortedNeighbourds.Values.First();
            Vector3Int r = l[l.Count-1];
            l.RemoveAt(l.Count - 1);
            if(l.Count == 0)
            {
                sortedNeighbourds.Remove(sortedNeighbourds.Keys.First());
            }
            return r;
        }

        public IEnumerator BuildRelevantChunksParallelAround(IMarchingCubeChunk chunk)
        {
            CreateBuffersIfNeeded();
            foreach (var item in chunk.NeighbourIndices)
            {
                AddSortedNeighbour(0, item);
            }
            if (sortedNeighbourds.Count > 0)
            {
                yield return BuildRelevantChunksParallelAround();
            }
            end = DateTime.Now;
            Debug.Log("Total millis: " + (end - start).TotalMilliseconds);
            if (totalTriBuild >= maxTrianglesLeft)
            {
                Debug.Log("Aborted");
            }
            Debug.Log("Total triangles: " + totalTriBuild);

            Debug.Log($"Number of chunks: {Chunks.Count}");
            ReleaseBuffersIfNeeded();
        }

        private IEnumerator BuildRelevantChunksParallelAround()
        {
            Vector3Int next;
            bool isNextInProgress = false;

            do
            {
                next = RemoveFirst();
                isNextInProgress = HasChunkStartedAt(next);
            } while (isNextInProgress && sortedNeighbourds.Count > 0);

            if (!isNextInProgress)
            {
                CreateChunkParallelAt(next, OnChunkDoneCallBack);
            }

            if (totalTriBuild < maxTrianglesLeft)
            {
                while (sortedNeighbourds.Count == 0 && channeledChunks > 0)
                {
                    yield return null;
                }
                if (sortedNeighbourds.Count > 0)
                {
                    //yield return null;
                    yield return BuildRelevantChunksParallelAround();
                }
            }
        }

        protected void OnChunkDoneCallBack(IMarchingCubeChunk chunk)
        {
            channeledChunks--;
            foreach (Vector3Int v3 in chunk.NeighbourIndices)
            {
                float distance = (startPos - v3 * ChunkSize).sqrMagnitude;
                if (!Chunks.ContainsKey(v3) && distance < maxChunkSqrDistance)
                {
                    AddSortedNeighbour(distance, v3);
                }
            }
        }

        protected int channeledChunks = 0;

        public void CheckChunksAround(Vector3 v)
        {
            CreateBuffersIfNeeded();

            Vector3Int chunkIndex = PositionToCoord(v);

            SetActivationOfChunks(chunkIndex);

            Vector3Int index = new Vector3Int();
            for (int x = -NeededChunkAmount / 2; x < NeededChunkAmount / 2 + 1; x++)
            {
                index.x = x;
                for (int y = Mathf.Max(-NeededChunkAmount / 2, -NeededChunkAmount / 2); y < NeededChunkAmount / 2 + 1; y++)
                {
                    index.y = y;
                    for (int z = -NeededChunkAmount / 2; z < NeededChunkAmount / 2 + 1; z++)
                    {
                        index.z = z;
                        Vector3Int shiftedIndex = index + chunkIndex;
                        if (!chunks.ContainsKey(shiftedIndex))
                        {
                            CreateChunkAt(shiftedIndex);
                        }
                    }
                }
            }

            ReleaseBuffersIfNeeded();
        }


        protected IMarchingCubeChunk FindNonEmptyChunkAround(Vector3 pos)
        {
            bool isEmpty = true;
            CreateBuffersIfNeeded();
            Vector3Int chunkIndex = PositionToCoord(pos);
            IMarchingCubeChunk chunk = null;
            while (isEmpty)
            {
                chunk = CreateChunkAt(chunkIndex);
                isEmpty = chunk.IsEmpty;
                if (chunk.IsEmpty)
                {
                    if (chunk.IsCompletlySolid)
                    {
                        chunkIndex.y += 1;
                    }
                    else
                    {
                        chunkIndex.y -= 1;
                    }
                }
            }
            ReleaseBuffersIfNeeded();
            return chunk;
        }



        protected void SetActivationOfChunks(Vector3Int center)
        {
            int deactivatedChunkSqrDistance = DeactivatedChunkDistance;
            deactivatedChunkSqrDistance *= deactivatedChunkSqrDistance;
            foreach (KeyValuePair<Vector3Int, IMarchingCubeChunk> kv in chunks)
            {
                int sqrMagnitude = (kv.Key - center).sqrMagnitude;
                kv.Value.SetActive(sqrMagnitude <= deactivatedChunkSqrDistance);
            }
        }

        protected void CreateChunkParallelAt(Vector3Int p, Action<IMarchingCubeChunk> OnDone)
        {
            IMarchingCubeChunk chunk = GetThreadedChunkObjectAt(p);
            BuildChunkParallel(p, chunk, () => OnDone(chunk));
        }

        protected IMarchingCubeChunk CreateChunkAt(Vector3Int p)
        {
            IMarchingCubeChunk chunk = GetChunkObjectAt(p);
            BuildChunk(p, chunk);
            return chunk;
        }

        public bool TryGetReadyChunkAt(Vector3Int p, out IMarchingCubeChunk chunk)
        {
            if (chunks.TryGetValue(p, out chunk))
            {
                if (chunk.IsReady)
                {
                    return true;
                }
                else
                {
                    chunk = null;
                    return false;
                }
            }
            return false;
        }

        public bool HasChunkStartedAt(Vector3Int p)
        {
            IMarchingCubeChunk chunk;
            if (chunks.TryGetValue(p, out chunk))
            {
                return chunk.HasStarted;
            }
            return false;
        }

        protected IMarchingCubeChunk GetChunkObjectAt(Vector3Int p)
        {
            GameObject g = Instantiate(chunkPrefab, transform);
            g.name = $"Chunk({p.x},{p.y},{p.z})";
            //g.transform.position = p * CHUNK_SIZE;

            IMarchingCubeChunk chunk = g.GetComponent<IMarchingCubeChunk>();
            chunks.Add(p, chunk);
            chunk.ChunkOffset = p;
            return chunk;
        }

        protected IMarchingCubeChunk GetThreadedChunkObjectAt(Vector3Int p)
        {
            GameObject g = Instantiate(threadedChunkPrefab, transform);
            g.name = $"Chunk({p.x},{p.y},{p.z})";
            //g.transform.position = p * CHUNK_SIZE;

            IMarchingCubeChunk chunk = g.GetComponent<IMarchingCubeChunk>();
            chunks.Add(p, chunk);
            chunk.ChunkOffset = p;
            return chunk;
        }


        protected Vector3Int PositionToCoord(Vector3 pos)
        {
            Vector3Int result = new Vector3Int();

            for (int i = 0; i < 3; i++)
            {
                result[i] = (int)(pos[i] / PointsPerChunkAxis);
            }

            return result;
        }

        public int totalTriBuild;

        TriangleBuilder[] tris;// = new TriangleBuilder[CHUNK_VOLUME * 5];
        float[] pointsArray;

        private ComputeBuffer triangleBuffer;
        private ComputeBuffer pointsBuffer;
        private ComputeBuffer triCountBuffer;

        protected void BuildChunk(Vector3Int p, IMarchingCubeChunk chunk)
        {
            int numTris = DispatchAndGetShaderData(p, chunk);
            chunk.InitializeWithMeshData(chunkMaterial, tris, numTris, pointsArray, this, surfaceLevel);
        }

        protected void BuildChunkParallel(Vector3Int p, IMarchingCubeChunk chunk, Action OnDone)
        {
            int numTris = DispatchAndGetShaderData(p, chunk);
            channeledChunks++;
            chunk.InitializeWithMeshDataParallel(chunkMaterial, tris, numTris, pointsArray, this, surfaceLevel, OnDone);
        }

        protected int DispatchAndGetShaderData(Vector3Int p, IMarchingCubeChunk chunk)
        {
            Vector3 center = CenterFromChunkIndex(p);
            densityGenerator.Generate(pointsBuffer, PointsPerChunkAxis, 0, center, 1);

            int numVoxelsPerAxis = ChunkSize;
            int numThreadsPerAxis = Mathf.CeilToInt(numVoxelsPerAxis / (float)threadGroupSize);

            triangleBuffer.SetCounterValue(0);
            marshShader.SetBuffer(0, "points", pointsBuffer);
            marshShader.SetBuffer(0, "triangles", triangleBuffer);
            marshShader.SetInt("numPointsPerAxis", PointsPerChunkAxis);
            marshShader.SetFloat("surfaceLevel", surfaceLevel);
            marshShader.SetFloat("spacing", 1);
            marshShader.SetVector("centre", new Vector4(center.x, center.y, center.z));

            marshShader.Dispatch(0, numThreadsPerAxis, numThreadsPerAxis, numThreadsPerAxis);

            // Get number of triangles in the triangle buffer
            ComputeBuffer.CopyCount(triangleBuffer, triCountBuffer, 0);
            int[] triCountArray = { 0 };
            triCountBuffer.GetData(triCountArray);
            int numTris = triCountArray[0];

            // Get triangle data from shader

            tris = new TriangleBuilder[numTris];
            triangleBuffer.GetData(tris, 0, 0, numTris);

            pointsArray = new float[CHUNK_VOLUME];
            pointsBuffer.GetData(pointsArray, 0, 0, CHUNK_VOLUME);

            totalTriBuild += numTris;

            return numTris;
        }

        protected int buffersCreated = 0;

        protected void CreateBuffersIfNeeded()
        {
            buffersCreated++;
            if (buffersCreated > 1)
                return;
            int numPoints = PointsPerChunkAxis * PointsPerChunkAxis * PointsPerChunkAxis;
            int numVoxelsPerAxis = ChunkSize - 1;
            int numVoxels = numVoxelsPerAxis * numVoxelsPerAxis * numVoxelsPerAxis;
            int maxTriangleCount = numVoxels * 5;

            // Always create buffers in editor (since buffers are released immediately to prevent memory leak)
            // Otherwise, only create if null or if size has changed
            //if (!Application.isPlaying || (pointsBuffer == null || numPoints != pointsBuffer.count))
            //{
            //    if (Application.isPlaying)
            //    {
            //        ReleaseBuffers();
            //    }
            triangleBuffer = new ComputeBuffer(maxTriangleCount, TriangleBuilder.SIZE_OF_TRI_BUILD, ComputeBufferType.Append);
            pointsBuffer = new ComputeBuffer(numPoints, sizeof(float) * 1);
            triCountBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);

            //}
        }

        protected void ReleaseBuffersIfNeeded()
        {
            buffersCreated--;
            if (buffersCreated == 0)
            {
                if (triangleBuffer != null)
                {
                    triangleBuffer.Release();
                    pointsBuffer.Release();
                    triCountBuffer.Release();
                }
            }
        }

        public static Vector3 CenterFromChunkIndex(Vector3Int v)
        {
            return new Vector3(v.x * ChunkSize, v.y * ChunkSize, v.z * ChunkSize);
        }

        protected float PointSpacing => 1;

        public Dictionary<Vector3Int, IMarchingCubeChunk> Chunks => chunks;

        public void EditNeighbourChunksAt(Vector3Int chunkOffset, Vector3Int cubeOrigin, float delta)
        {
            foreach (Vector3Int v in cubeOrigin.GetAllCombination())
            {
                bool allActiveIndicesHaveOffset = true;
                Vector3Int offsetVector = new Vector3Int();
                for (int i = 0; i < 3 && allActiveIndicesHaveOffset; i++)
                {
                    if (v[i] != int.MinValue)
                    {
                        //offset is in range -1 to 1
                        int offset = Mathf.CeilToInt((cubeOrigin[i] / (ChunkSize - 2f)) - 1);
                        allActiveIndicesHaveOffset = offset != 0;
                        offsetVector[i] = offset;
                    }
                    else
                    {
                        offsetVector[i] = 0;
                    }
                }
                if (allActiveIndicesHaveOffset)
                {
                    Debug.Log("Found neighbour with offset " + offsetVector);
                    IMarchingCubeChunk neighbourChunk;
                    if (chunks.TryGetValue(chunkOffset + offsetVector, out neighbourChunk))
                    {
                        EditNeighbourChunkAt(neighbourChunk, cubeOrigin, offsetVector, delta);
                    }
                }
            }
        }

        public void EditNeighbourChunkAt(IMarchingCubeChunk chunk, Vector3Int original, Vector3Int offset, float delta)
        {
            if (chunk is IMarchingCubeInteractableChunk interactable)
            {
                Vector3Int newChunkCubeIndex = (original + offset).Map(f => MathExt.FloorMod(f, ChunkSize));
                MarchingCubeEntity e = interactable.GetEntityAt(newChunkCubeIndex.x, newChunkCubeIndex.y, newChunkCubeIndex.z);
                interactable.EditPointsNextToChunk(chunk, e, offset, delta);
            }
            else
            {
                Debug.LogWarning("Neighbour chunk is not interactable!");
            }
        }

        void OnDestroy()
        {
            if (Application.isPlaying)
            {
                ReleaseBuffersIfNeeded();
            }
        }

    }
}