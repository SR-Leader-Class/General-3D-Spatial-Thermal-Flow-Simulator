using UnityEngine;
using System.Collections;

public class LB_CameraMove : MonoBehaviour {

	[Tooltip("滑鼠視角旋轉靈敏度。")]
	public float cameraSensitivity = 90;
	[Tooltip("按下 Q 或 E 時沿垂直方向移動的速度。")]
	public float climbSpeed = 4;
	[Tooltip("一般前後左右移動速度。")]
	public float normalMoveSpeed = 10;
	[Tooltip("按住 Ctrl 時套用的慢速倍率。")]
	public float slowMoveFactor = 0.25f;
	[Tooltip("按住 Shift 時套用的加速倍率。")]
	public float fastMoveFactor = 3;

	private float rotationX = 0.0f;
	private float rotationY = 0.0f;
	[Tooltip("是否顯示並釋放滑鼠游標。")]
	public bool showCursor = true;
	bool isActive = true;

	void Start ()
	{
		if (!showCursor)
			Cursor.lockState = CursorLockMode.Locked;
		else
			Cursor.lockState = CursorLockMode.None;

		Cursor.visible = showCursor;
	}

	void Update ()
	{

		if(Input.GetKeyDown(KeyCode.F))
			isActive = !isActive;

		if(!isActive)
			return;

		rotationX += Input.GetAxis("Mouse X") * cameraSensitivity * Time.deltaTime;
		rotationY += Input.GetAxis("Mouse Y") * cameraSensitivity * Time.deltaTime;
		rotationY = Mathf.Clamp (rotationY, -90, 90);

		transform.localRotation = Quaternion.AngleAxis(rotationX, Vector3.up);
		transform.localRotation *= Quaternion.AngleAxis(rotationY, Vector3.left);

		if (Input.GetKey (KeyCode.LeftShift) || Input.GetKey (KeyCode.RightShift))
		{
			transform.position += transform.forward * (normalMoveSpeed * fastMoveFactor) * Input.GetAxis("Vertical") * Time.deltaTime;
			transform.position += transform.right * (normalMoveSpeed * fastMoveFactor) * Input.GetAxis("Horizontal") * Time.deltaTime;
		}
		else if (Input.GetKey (KeyCode.LeftControl) || Input.GetKey (KeyCode.RightControl))
		{
			transform.position += transform.forward * (normalMoveSpeed * slowMoveFactor) * Input.GetAxis("Vertical") * Time.deltaTime;
			transform.position += transform.right * (normalMoveSpeed * slowMoveFactor) * Input.GetAxis("Horizontal") * Time.deltaTime;
		}
		else
		{
			transform.position += transform.forward * normalMoveSpeed * Input.GetAxis("Vertical") * Time.deltaTime;
			transform.position += transform.right * normalMoveSpeed * Input.GetAxis("Horizontal") * Time.deltaTime;
		}


		if (Input.GetKey (KeyCode.E)) {transform.position += transform.up * climbSpeed * Time.deltaTime;}
		if (Input.GetKey (KeyCode.Q)) {transform.position -= transform.up * climbSpeed * Time.deltaTime;}

	}
}
