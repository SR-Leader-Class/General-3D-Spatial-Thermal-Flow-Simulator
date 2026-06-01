using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class AlarmManager : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text alarmText;

    private readonly List<CleanroomZone> subscribedZones = new List<CleanroomZone>();
    private bool alarmsDirty;

    private void OnEnable()
    {
        SubscribeToZones();
        alarmsDirty = true;
    }

    private void OnDisable()
    {
        UnsubscribeFromZones();
    }

    private void LateUpdate()
    {
        if (!alarmsDirty)
            return;

        alarmsDirty = false;
        RefreshAlarms();
    }

    private void RefreshAlarms()
    {
        CleanroomZone[] zones = FindObjectsByType<CleanroomZone>(FindObjectsSortMode.None);
        List<string> alarmLines = new List<string>();

        foreach (CleanroomZone zone in zones)
        {
            if (zone.CurrentStatus == CleanroomStatus.Normal)
                continue;

            string reason = GetMainReason(zone);

            alarmLines.Add(
                $"[{zone.CurrentStatus}] {zone.displayName} - {reason} (Risk {zone.CurrentRiskScore:F2})"
            );
        }

        if (alarmText == null)
            return;

        if (alarmLines.Count == 0)
        {
            alarmText.text = "No active alarms.";
        }
        else
        {
            alarmText.text = string.Join("\n", alarmLines);
        }
    }

    private string GetMainReason(CleanroomZone zone)
    {
        if (zone == null)
            return "Zone unavailable";

        if (!string.IsNullOrEmpty(zone.PrimaryRiskReason))
            return zone.PrimaryRiskReason;

        return "Risk score exceeded threshold";
    }

    private void SubscribeToZones()
    {
        UnsubscribeFromZones();

        CleanroomZone[] zones = FindObjectsByType<CleanroomZone>(FindObjectsSortMode.None);

        foreach (CleanroomZone zone in zones)
        {
            if (zone == null)
                continue;

            zone.StatusChanged += HandleZoneStatusChanged;
            zone.ZoneDataUpdated += HandleZoneDataUpdated;
            subscribedZones.Add(zone);
        }
    }

    private void UnsubscribeFromZones()
    {
        foreach (CleanroomZone zone in subscribedZones)
        {
            if (zone == null)
                continue;

            zone.StatusChanged -= HandleZoneStatusChanged;
            zone.ZoneDataUpdated -= HandleZoneDataUpdated;
        }

        subscribedZones.Clear();
    }

    private void HandleZoneStatusChanged(CleanroomZone zone, CleanroomStatus status)
    {
        alarmsDirty = true;
    }

    private void HandleZoneDataUpdated(CleanroomZone zone)
    {
        if (zone == null)
            return;

        if (zone.CurrentStatus != CleanroomStatus.Normal)
            alarmsDirty = true;
    }
}
