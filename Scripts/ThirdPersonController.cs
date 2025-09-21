using UnityEngine;
#if ENABLE_INPUT_SYSTEM 
using UnityEngine.InputSystem;
#endif

/* Nota: las animaciones se llaman a través del controlador tanto para el personaje como para la cápsula utilizando comprobaciones de nulos del animador
 */

namespace StarterAssets
{
    [RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM 
    [RequireComponent(typeof(PlayerInput))]
#endif
    public class ThirdPersonController : MonoBehaviour
    {
        [Header("Player")]
        [Tooltip("Velocidad de movimiento del personaje en m/s")]
        public float MoveSpeed = 2.0f;

        [Tooltip("Velocidad de sprint del personaje en m/s")]
        public float SprintSpeed = 5.335f;

        [Tooltip("Qué tan rápido el personaje gira para encarar la dirección de movimiento")]
        [Range(0.0f, 0.3f)]
        public float RotationSmoothTime = 0.12f;

        [Tooltip("Aceleración y desaceleración")]
        public float SpeedChangeRate = 10.0f;

        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)]
        [Tooltip("La altura a la que el jugador puede saltar")]
        public float JumpHeight = 1.2f;

        [Tooltip("El personaje usa su propio valor de gravedad. El valor por defecto del motor es -9.81f")]
        public float Gravity = -15.0f;

        [Space(10)]
        [Tooltip("Tiempo requerido para poder saltar de nuevo. Establecer en 0f para saltar instantáneamente")]
        public float JumpTimeout = 0.50f;

        [Tooltip("Tiempo requerido para pasar antes de entrar en el estado de caída. Útil para bajar escaleras")]
        public float FallTimeout = 0.15f;

        [Header("Player Grounded")]
        [Tooltip("Si el personaje está en el suelo o no. No es parte del chequeo de 'grounded' del CharacterController")]
        public bool Grounded = true;

        [Tooltip("Útil para terreno irregular")]
        public float GroundedOffset = -0.14f;

        [Tooltip("El radio del chequeo de 'grounded'. Debe coincidir con el radio del CharacterController")]
        public float GroundedRadius = 0.28f;

        [Tooltip("Qué capas usa el personaje como suelo")]
        public LayerMask GroundLayers;

        [Header("Cinemachine")]
        [Tooltip("El objetivo de seguimiento en la cámara virtual de Cinemachine que la cámara seguirá")]
        public GameObject CinemachineCameraTarget;

        [Tooltip("Cuántos grados puedes mover la cámara hacia arriba")]
        public float TopClamp = 70.0f;

        [Tooltip("Cuántos grados puedes mover la cámara hacia abajo")]
        public float BottomClamp = -30.0f;

        [Tooltip("Grados adicionales para anular la cámara. Útil para afinar la posición de la cámara cuando está bloqueada")]
        public float CameraAngleOverride = 0.0f;

        [Tooltip("Para bloquear la posición de la cámara en todos los ejes")]
        public bool LockCameraPosition = false;

        // cinemachine
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;

        // player
        private float _speed;
        private float _animationBlend;
        private float _targetRotation = 0.0f;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _terminalVelocity = 53.0f;

        // timeout deltatime
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // animation IDs
        private int _animIDSpeed;
        private int _animIDGrounded;
        private int _animIDJump;
        private int _animIDFreeFall;
        private int _animIDMotionSpeed;
        private int _animIDCrouching; // Agrega el ID para el parámetro de agacharse

        // Variables para agacharse
        private float _originalHeight;
        private float _originalCenterY;
        private bool _isCrouching = false;

#if ENABLE_INPUT_SYSTEM 
        private PlayerInput _playerInput;
#endif
        private Animator _animator;
        private CharacterController _controller;
        private StarterAssetsInputs _input;
        private GameObject _mainCamera;

        private const float _threshold = 0.01f;

        private bool _hasAnimator;

        private bool IsCurrentDeviceMouse
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return _playerInput.currentControlScheme == "KeyboardMouse";
#else
                return false;
#endif
            }
        }

        private void Awake()
        {
            // obtener una referencia a nuestra cámara principal
            if (_mainCamera == null)
            {
                _mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
            }
        }

        private void Start()
        {
            _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;

            _hasAnimator = TryGetComponent(out _animator);
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<StarterAssetsInputs>();

            // --- CÓDIGO AGREGADO: Almacena la altura y el centro originales ---
            _originalHeight = _controller.height;
            _originalCenterY = _controller.center.y;
            // ------------------------------------------------------------------

#if ENABLE_INPUT_SYSTEM 
            _playerInput = GetComponent<PlayerInput>();
#else
            Debug.LogError( "Starter Assets package is missing dependencies. Please use Tools/Starter Assets/Reinstall Dependencies to fix it");
#endif

            AssignAnimationIDs();

            // restablecer nuestros timeouts al inicio
            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;
        }

        private void Update()
        {
            _hasAnimator = TryGetComponent(out _animator);

            JumpAndGravity();
            GroundedCheck();

            // --- CÓDIGO AGREGADO: Lógica de agacharse y ajuste del CharacterController ---
            if (_input.crouch)
            {
                _isCrouching = true;
                _controller.height = 1.0f;
                _controller.center = new Vector3(_controller.center.x, 0.5f, _controller.center.z);
            }
            else
            {
                _isCrouching = false;
                _controller.height = _originalHeight;
                _controller.center = new Vector3(_controller.center.x, _originalCenterY, _controller.center.z);
            }

            _animator.SetBool("IsCrouching", _isCrouching);
            // ---------------------------------------------------------------------------------

            Move();
        }

        private void LateUpdate()
        {
            CameraRotation();
        }

        private void AssignAnimationIDs()
        {
            _animIDSpeed = Animator.StringToHash("Speed");
            _animIDGrounded = Animator.StringToHash("Grounded");
            _animIDJump = Animator.StringToHash("Jump");
            _animIDFreeFall = Animator.StringToHash("FreeFall");
            _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
        }

        private void GroundedCheck()
        {
            // establecer la posición de la esfera, con offset
            Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset,
                transform.position.z);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers,
                QueryTriggerInteraction.Ignore);

            // actualizar el animador si se usa un personaje
            if (_hasAnimator)
            {
                _animator.SetBool(_animIDGrounded, Grounded);
            }
        }

        private void CameraRotation()
        {
            // si hay un input y la posición de la cámara no está fija
            if (_input.look.sqrMagnitude >= _threshold && !LockCameraPosition)
            {
                //No multiplicar el input del ratón por Time.deltaTime;
                float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;

                _cinemachineTargetYaw += _input.look.x * deltaTimeMultiplier;
                _cinemachineTargetPitch += _input.look.y * deltaTimeMultiplier;
            }

            // ajustar nuestras rotaciones para que nuestros valores estén limitados a 360 grados
            _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
            _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

            // Cinemachine seguirá a este objetivo
            CinemachineCameraTarget.transform.rotation = Quaternion.Euler(_cinemachineTargetPitch + CameraAngleOverride,
                _cinemachineTargetYaw, 0.0f);
        }

        private void Move()
        {
            // establecer la velocidad objetivo basada en la velocidad de movimiento, la velocidad de sprint y si se presiona sprint
            float targetSpeed = _input.sprint ? SprintSpeed : MoveSpeed;

            // una aceleración y desaceleración simplistas diseñadas para ser fáciles de eliminar, reemplazar o iterar

            // nota: el operador == de Vector2 usa aproximación, por lo que no es propeno a errores de punto flotante, y es más barato que la magnitud
            // si no hay input, establecer la velocidad objetivo en 0
            if (_input.move == Vector2.zero) targetSpeed = 0.0f;

            // una referencia a la velocidad horizontal actual del jugador
            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f, _controller.velocity.z).magnitude;

            float speedOffset = 0.1f;
            float inputMagnitude = _input.analogMovement ? _input.move.magnitude : 1f;

            // acelerar o desacelerar a la velocidad objetivo
            if (currentHorizontalSpeed < targetSpeed - speedOffset ||
                currentHorizontalSpeed > targetSpeed + speedOffset)
            {
                // crea un resultado curvo en lugar de uno lineal, lo que da un cambio de velocidad más orgánico
                // nota: T en Lerp está restringido, por lo que no necesitamos restringir nuestra velocidad
                _speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude,
                    Time.deltaTime * SpeedChangeRate);

                // redondear la velocidad a 3 decimales
                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = targetSpeed;
            }

            _animationBlend = Mathf.Lerp(_animationBlend, targetSpeed, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;

            // normalizar la dirección del input
            Vector3 inputDirection = new Vector3(_input.move.x, 0.0f, _input.move.y).normalized;

            // nota: el operador != de Vector2 usa aproximación, por lo que no es propeno a errores de punto flotante, y es más barato que la magnitud
            // si hay un input de movimiento, rotar al jugador cuando se está moviendo
            if (_input.move != Vector2.zero)
            {
                _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg +
                                    _mainCamera.transform.eulerAngles.y;
                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation, ref _rotationVelocity,
                    RotationSmoothTime);

                // rotar para encarar la dirección del input relativa a la posición de la cámara
                transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
            }


            Vector3 targetDirection = Quaternion.Euler(0.0f, _targetRotation, 0.0f) * Vector3.forward;

            // mover al jugador
            _controller.Move(targetDirection.normalized * (_speed * Time.deltaTime) +
                                new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime);

            // actualizar el animador si se usa un personaje
            if (_hasAnimator)
            {
                _animator.SetFloat(_animIDSpeed, _animationBlend);
                _animator.SetFloat(_animIDMotionSpeed, inputMagnitude);
            }
        }

        private void JumpAndGravity()
        {
            if (Grounded)
            {
                // restablecer el temporizador de caída
                _fallTimeoutDelta = FallTimeout;

                // actualizar el animador si se usa un personaje
                if (_hasAnimator)
                {
                    _animator.SetBool(_animIDJump, false);
                    _animator.SetBool(_animIDFreeFall, false);
                }

                // detener nuestra velocidad de caída infinita cuando está en el suelo
                if (_verticalVelocity < 0.0f)
                {
                    _verticalVelocity = -2f;
                }

                // Salto
                if (_input.jump && _jumpTimeoutDelta <= 0.0f)
                {
                    // la raíz cuadrada de H * -2 * G = cuánta velocidad se necesita para alcanzar la altura deseada
                    _verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);

                    // actualizar el animador si se usa un personaje
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDJump, true);
                    }
                }

                // tiempo de espera para saltar
                if (_jumpTimeoutDelta >= 0.0f)
                {
                    _jumpTimeoutDelta -= Time.deltaTime;
                }
            }
            else
            {
                // restablecer el temporizador de salto
                _jumpTimeoutDelta = JumpTimeout;

                // tiempo de espera para caer
                if (_fallTimeoutDelta >= 0.0f)
                {
                    _fallTimeoutDelta -= Time.deltaTime;
                }
                else
                {
                    // actualizar el animador si se usa un personaje
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDFreeFall, true);
                    }
                }

              
                _input.jump = false;
            }

            if (_verticalVelocity < _terminalVelocity)
            {
                _verticalVelocity += Gravity * Time.deltaTime;
            }
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

            if (Grounded) Gizmos.color = transparentGreen;
            else Gizmos.color = transparentRed;

            // cuando está seleccionado, dibujar un gizmo en la posición y con el radio del colisionador de grounded
            Gizmos.DrawSphere(
                new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z),
                GroundedRadius);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                if (FootstepAudioClips.Length > 0)
                {
                    var index = Random.Range(0, FootstepAudioClips.Length);
                    AudioSource.PlayClipAtPoint(FootstepAudioClips[index], transform.TransformPoint(_controller.center), FootstepAudioVolume);
                }
            }
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                AudioSource.PlayClipAtPoint(LandingAudioClip, transform.TransformPoint(_controller.center), FootstepAudioVolume);
            }
        }
    }
}