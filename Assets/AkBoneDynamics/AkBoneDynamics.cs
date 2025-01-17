﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AkBoneDynamics
{
    public class AkBoneDynamics : MonoBehaviour
    {
        #region Sub-Class

        // Particle class
        private class Particle
        {
            // Particle Parameters
            public Vector2Int m_Address;
            public Transform m_Transform;
            public Vector3 m_InitialLocalPosition;
            public Vector3 m_CurrentPosition;
            public Vector3 m_NextPosition;
            public Vector3 m_Velocity;

            public float m_Damping = 0f;
            public float m_Elasticity = 0f;
            public float m_Radius = 0f;
            //public float m_Mass;

            // Parent Parameters
            public Transform m_ParentTransform;
            public Quaternion m_ParentInitialLocalRotation;

            // Keep Length (Update m_NextPosition)
            public void KeepLength()
            {
                m_NextPosition = m_ParentTransform.position + (m_NextPosition - m_ParentTransform.position).normalized * m_InitialLocalPosition.magnitude;
            }
        }

        // Constraint class
        private class Constraint
        {
            public Particle m_A;
            public Particle m_B;
            public float m_Distance;

            // Initialize
            public Constraint(Particle p1, Particle p2)
            {
                m_A = p1;
                m_B = p2;
                m_Distance = (p1.m_Transform.position - p2.m_Transform.position).magnitude;
            }

            // A,Bのm_NextPositionを動かして元の長さに戻す。
            public void Calculate(float weight)
            {
                float nextDisatance = (m_A.m_NextPosition - m_B.m_NextPosition).magnitude;
                float diffelence = nextDisatance - m_Distance;
                Vector3 v = (m_A.m_NextPosition - m_B.m_NextPosition).normalized;

                var dpA = -0.5f * diffelence * v;
                var dpB = 0.5f * diffelence * v;

                m_A.m_NextPosition += dpA * weight;
                m_B.m_NextPosition += dpB * weight;
            }
        }

        #endregion

        #region Inspector Variables

        [Header("Bones")]
        [SerializeField] private List<Transform> m_RootBones = null;
        [SerializeField] private List<Transform> m_ExcludeBones = null;
        
        [Range(0, 1)] [SerializeField] private float m_Damping = 0.1f;
        [Range(0, 10)] [SerializeField] private float m_Elasticity = 1f;
        [SerializeField] private float m_Radius = 0.5f;
        [SerializeField] private AnimationCurve m_RadiusLevel = null;

        [Header("External Forces")]
        [SerializeField] private bool m_EnableExternalForce = false;
        [SerializeField] private Vector3 m_Gravity = new Vector3(0f, -9.8f, 0f);

        [Header("Limitation")]
        [SerializeField] private bool m_EnableAngleLimit = false;
        [Range(0, 180)] [SerializeField] private float m_AngleLimit = 90f;

        [Header("Collisions")]
        [SerializeField] private List<AkBDCollider> m_Colliders = null;

        [Header("Constraints")]
        [SerializeField] private bool m_EnableConstraints = false;
        [Range(0, 1)] [SerializeField] private float m_ConstraintStrength = 1f;
        [SerializeField] private bool m_CloseConstraints = false;

        [Header("Cancel Translation")]
        [SerializeField] private Transform m_TransformRoot = null; // これより上階層の移動値を相殺する。

        [Header("Performance")]
        [Range(0, 10)] [SerializeField] private int m_CollideIteration = 1;
        [Range(0, 10)] [SerializeField] private int m_ConstraintIteration = 1;

        [Header("Other Settings")]
        [SerializeField] private bool m_DrawGizmo = true;
        [SerializeField] private bool m_Debug = true;
        [SerializeField] private string m_Comment;
        
        #endregion

        #region Fields

        List<Particle> m_Particles = new List<Particle>();
        private Vector2Int m_AddressMax = Vector2Int.zero;
        
        List<Constraint> m_Constraints = new List<Constraint>();

        // 相殺する移動値のVector3
        private Vector3 m_RootPosition;

        #endregion

        #region Initialize

        // 質点の初期化（再帰で子探索）
        void AddParticle(Transform b, int i, int j)
        {
            j++;

            // Itterate through all child bones
            for (int c = 0; c < b.childCount; c++)
            {
                var child = b.GetChild(c);

                // Only add particles to bone-chains that aren't part of our ExcludedBones list
                if (!m_ExcludeBones.Contains(child))
                {
                    var p = new Particle
                    {
                        m_ParentTransform = b,
                        m_ParentInitialLocalRotation = b.transform.localRotation,

                        m_Address = new Vector2Int(i, j),
                        m_Transform = child,
                        m_InitialLocalPosition = child.transform.localPosition,
                        m_CurrentPosition = child.transform.position,
                        m_NextPosition = child.transform.position,
                        m_Velocity = Vector3.zero
                    };
                    m_Particles.Add(p);

                    m_AddressMax = Vector2Int.Max(m_AddressMax, p.m_Address);  
                    AddParticle(child, i, j);
                }
            }
        }

        // 質点の初期化
        void InitializeParticles()
        {
            m_Particles.Clear();

            if (m_RootBones != null)
            {
                for (int i = 0; i < m_RootBones.Count; i++)
                {
                    AddParticle(m_RootBones[i], i, 0);
                }
            }
        }

        // 質点パラメータの更新
        void UpdateParticleParams()
        {
            foreach (Particle p in m_Particles)
            {
                p.m_Damping = m_Damping;
                p.m_Elasticity = m_Elasticity;
                p.m_Radius = m_Radius;

                var ratio = (float)p.m_Address.y / (float)m_AddressMax.y;
                if (m_RadiusLevel != null && m_RadiusLevel.keys.Length > 0)
                {
                    p.m_Radius *= Mathf.Clamp01(m_RadiusLevel.Evaluate(ratio));
                }
            }
        }

        // コンストレイントの初期化
        void InitializeConstraints()
        {
            m_Constraints.Clear();

            for (int i = 0; i < m_Particles.Count; i++)
            {
                // 比較用アドレス
                var constraintAddress = m_Particles[i].m_Address + Vector2Int.right;
                if (m_CloseConstraints && m_Particles[i].m_Address.x == m_AddressMax.x)
                {
                    constraintAddress *= Vector2Int.up;
                }

                // コンストレイント対象の探索
                for (int j = 0; j < m_Particles.Count; j++)
                {
                    if (m_EnableConstraints)
                    {
                        if (m_Particles[j].m_Address == constraintAddress)
                        {
                            var c = new Constraint(m_Particles[i], m_Particles[j]);
                            m_Constraints.Add(c);
                        }
                    }
                }
            }
        }

        #endregion

        #region Events

        void Awake()
        {
            if (m_TransformRoot != null)
            {
                m_RootPosition = m_TransformRoot.transform.position;
            }

            InitializeParticles();
            UpdateParticleParams();
            InitializeConstraints();

            /*
            var msg = "<color=cyan>";
            msg += $"[AkBoneDynamics] \"{m_Comment}\" (";
            msg += $"Particles: {m_Particles.Count.ToString()} / ";
            msg += $"MaxAddress: {m_AddressMax.ToString()} / ";
            msg += $"Constraints: {m_Constraints.Count.ToString()}";
            msg += ")</color>";
            Debug.Log(msg);
            */
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            UpdateParticles(dt);
        }


        #endregion

        #region Core

        // axisベクトルを軸にvベクトルをangle度回転、回転後のベクトルを返す。
        private Vector3 RotateVectorAngle(Vector3 v, Vector3 axisNormalized, float angle)
        {
            var projectVector = Vector3.Project(v, axisNormalized);
            var vecP = v - projectVector;
            var vecQ = Vector3.Cross(axisNormalized, vecP);

            var s = Mathf.Cos(angle * Mathf.Deg2Rad);
            var t = Mathf.Sin(angle * Mathf.Deg2Rad);

            return projectVector + (vecP * s) + (vecQ * t);
        }

        private void UpdateParticles(float dt)
        {
            // Cancel Root Translation
            if (m_TransformRoot != null)
            {
                var rootTranslation = m_TransformRoot.transform.position - m_RootPosition;
                m_RootPosition = m_TransformRoot.transform.position;

                foreach (Particle p in m_Particles)
                {
                    p.m_CurrentPosition += rootTranslation;
                }
            }

            // Core
            foreach (Particle p in m_Particles)
            {
                var parentPosition = p.m_ParentTransform.position;

                // Velocity Damping
                p.m_Velocity = p.m_Velocity * (1.0f - p.m_Damping);

                // External Force
                var externalForce = m_Gravity;

                // Verlet Integration
                p.m_NextPosition = p.m_CurrentPosition + (p.m_Velocity * dt) + (m_EnableExternalForce ? (externalForce * dt * dt) : Vector3.zero);
                
                // Keep Length
                p.KeepLength();

                // Keep Direction
                p.m_ParentTransform.localRotation = p.m_ParentInitialLocalRotation;
                p.m_NextPosition += (p.m_Transform.position - p.m_NextPosition) * p.m_Elasticity * dt;
                p.KeepLength();

                // debug (Initial Direction Vector)
                if (m_Debug) {Debug.DrawLine(parentPosition, p.m_Transform.position, Color.blue, 0, true);}

                // Angle Limit
                if (m_EnableAngleLimit)
                {
                    var initialVec = (p.m_Transform.position - parentPosition).normalized;
                    var nextPosVec = (p.m_NextPosition - parentPosition).normalized;
                    var vecAngle = Vector3.Angle(nextPosVec, initialVec);
                    var overAngle = vecAngle - m_AngleLimit;
                    if (overAngle > 0)
                    {
                        var axisVec = Vector3.Cross(nextPosVec, initialVec).normalized;
                        var limitedNextPosVec = RotateVectorAngle(nextPosVec, axisVec, overAngle);
                        p.m_NextPosition = parentPosition + limitedNextPosVec * p.m_InitialLocalPosition.magnitude;

                        // debug (Limited Vector)
                        if (m_Debug) {Debug.DrawLine(parentPosition, p.m_NextPosition, Color.cyan, 0, true);}
                    }
                }

                // Collision
                for (int i = 0; i < m_CollideIteration; i++)
                {
                    foreach (var col in m_Colliders)
                    {
                        // Adjust nextPosision
                        var rtn = col.CollisionDetection(p.m_Radius, p.m_NextPosition);
                        p.m_NextPosition = rtn.newCenter;
                        if (rtn.isCollide)
                        {
                            p.KeepLength();
                        }
                    }
                }

                // Apply to Parent Rotation
                var r = Quaternion.FromToRotation((p.m_ParentTransform.rotation * p.m_InitialLocalPosition), (p.m_CurrentPosition - parentPosition));
                p.m_ParentTransform.rotation = r * p.m_ParentTransform.rotation;
            }

            // Constraints
            for (int i = 0; i < m_ConstraintIteration; i++)
            {
                if (m_EnableConstraints && (m_ConstraintStrength != 0f) ? true : false)
                {
                    foreach (Constraint c in m_Constraints)
                    {
                        c.Calculate(m_ConstraintStrength);
                    }
                }
            }

            // Updates
            foreach (Particle p in m_Particles)
            {
                // Keep Length
                p.KeepLength();

                // Apply to Parent Rotation
                var r = Quaternion.FromToRotation((p.m_ParentTransform.rotation * p.m_InitialLocalPosition), (p.m_CurrentPosition - p.m_ParentTransform.position));
                p.m_ParentTransform.rotation = r * p.m_ParentTransform.rotation;

                // Update Velocity
                p.m_Velocity = (p.m_NextPosition - p.m_CurrentPosition) / dt;
                p.m_CurrentPosition = p.m_NextPosition;
            }
        }

        #endregion

        #region Editor Events

        void OnValidate()
        {
            m_Radius = Mathf.Max(m_Radius, 0);

            UpdateParticleParams();
        }

        void OnDrawGizmos()
        {
            if (m_DrawGizmo)
            {
                if (Application.isEditor && !Application.isPlaying && transform.hasChanged)
                {
                    InitializeParticles();
                    UpdateParticleParams();
                    InitializeConstraints();
                }

                foreach (Particle p in m_Particles)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawWireSphere(p.m_Transform.position, p.m_Radius);
                    Gizmos.color = Color.white;
                    Gizmos.DrawLine(p.m_ParentTransform.position, p.m_Transform.position);
                }

                if (m_EnableConstraints && (m_ConstraintStrength != 0f) ? true : false)
                {
                    foreach (Constraint c in m_Constraints)
                    {
                        Gizmos.color = Color.yellow * new Color(1.0f, 1.0f, 1.0f, m_ConstraintStrength); ;
                        Gizmos.DrawLine(c.m_A.m_Transform.position, c.m_B.m_Transform.position);
                    }
                }
            }
        }

        #endregion
    }
}