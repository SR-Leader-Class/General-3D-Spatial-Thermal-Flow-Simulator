using UnityEngine;

public class CleanroomDoor : MonoBehaviour
{
    [Header("Door Info")]
    public string doorId = "DOOR_001";
    public string displayName = "ISO7 to ISO5 Door";

    [Header("Zones")]
    public CleanroomZone highCleanZone;
    public CleanroomZone lowCleanZone;

    [Header("Door State")]
    public bool isOpen;
    public float openAngle = 90f;
    public float closedAngle = 0f;
    public float rotateSpeed = 5f;

    [Header("Contamination")]
    public float contaminationRate = 1500f;
    public float pressureDropRate = 2f;
    public float pressureRecoverySpeed = 0.5f;
    public float crossZoneImpactFactor = 0.15f;
    public ParticleSystem contaminationEffect;

    private Quaternion closedRotation;
    private Quaternion openRotation;

    private void Start()
    {
        closedRotation = Quaternion.Euler(0f, closedAngle, 0f);
        openRotation = Quaternion.Euler(0f, openAngle, 0f);

        if (contaminationEffect != null)
            contaminationEffect.Stop();
    }

    private void Update()
    {
        UpdateDoorVisual();
        UpdateContamination();
    }

    private void OnMouseDown()
    {
        ToggleDoor();
    }

    public void ToggleDoor()
    {
        isOpen = !isOpen;

        if (contaminationEffect != null)
        {
            if (isOpen)
                contaminationEffect.Play();
            else
                contaminationEffect.Stop();
        }
    }

    private void UpdateDoorVisual()
    {
        Quaternion targetRotation = isOpen ? openRotation : closedRotation;

        transform.localRotation = Quaternion.Slerp(
            transform.localRotation,
            targetRotation,
            Time.deltaTime * rotateSpeed
        );
    }

    private void UpdateContamination()
    {
        if (highCleanZone == null)
            return;

        if (isOpen)
        {
            if (lowCleanZone != null)
                lowCleanZone.AddDoorOpenTime(Time.deltaTime);

            highCleanZone.AddDoorOpenTime(Time.deltaTime);

            float pressureGradient = 0f;

            if (lowCleanZone != null)
                pressureGradient = Mathf.Max(0f, lowCleanZone.pressure - highCleanZone.pressure);

            float contaminationAmount =
                contaminationRate *
                (1f + pressureGradient * crossZoneImpactFactor) *
                Time.deltaTime;

            highCleanZone.AddContamination(contaminationAmount);

            highCleanZone.pressure -= pressureDropRate * Time.deltaTime;
            highCleanZone.pressure = Mathf.Max(-5f, highCleanZone.pressure);

            if (lowCleanZone != null)
            {
                lowCleanZone.pressure = Mathf.Lerp(
                    lowCleanZone.pressure,
                    lowCleanZone.targetPressure,
                    Time.deltaTime * pressureRecoverySpeed * 0.5f
                );
            }
        }
        else
        {
            if (lowCleanZone != null)
                lowCleanZone.ResetDoorOpenTime();

            highCleanZone.ResetDoorOpenTime();
            RecoverZonePressure(highCleanZone, pressureRecoverySpeed);

            if (lowCleanZone != null)
                RecoverZonePressure(lowCleanZone, pressureRecoverySpeed * 0.5f);
        }
    }

    private void RecoverZonePressure(CleanroomZone zone, float recoverySpeed)
    {
        if (zone == null)
            return;

        zone.pressure = Mathf.Lerp(
            zone.pressure,
            zone.targetPressure,
            Time.deltaTime * recoverySpeed
        );
    }
}
