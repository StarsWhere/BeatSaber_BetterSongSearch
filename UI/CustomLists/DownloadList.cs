using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BetterSongSearch.Util;
using HMUI;
using System.Reflection;
using TMPro;
using UnityEngine;
using static BetterSongSearch.UI.DownloadHistoryView;
using static BetterSongSearch.UI.DownloadHistoryView.DownloadHistoryEntry;

namespace BetterSongSearch.UI.CustomLists {
	static class DownloadListTableData {
		const string ReuseIdentifier = "REUSECustomDownloadListTableCell";

		public static CustomDownloadListTableCell GetCell(TableView tableView) {
			var tableCell = tableView.DequeueReusableCellForIdentifier(ReuseIdentifier);

			if(tableCell == null) {
				tableCell = new GameObject("CustomDownloadListTableCell", typeof(Touchable)).AddComponent<CustomDownloadListTableCell>();
				tableCell.transform.SetParent(tableView.transform,false);
				tableCell.interactable = true;

				tableCell.reuseIdentifier = ReuseIdentifier;
				BSMLParser.Instance.Parse(
					Utilities.GetResourceContent(Assembly.GetExecutingAssembly(), "BetterSongSearch.UI.CustomLists.DownloadListCell.bsml"),
					tableCell.gameObject, tableCell
				);
				BssTypography.Apply(tableCell.gameObject);
			}

			return (CustomDownloadListTableCell)tableCell;
		}
	}

	class CustomDownloadListTableCell : TableCell {
		[UIComponent("songName")] TextMeshProUGUI songName = null;
		[UIComponent("levelAuthorName")] TextMeshProUGUI levelAuthorName = null;
		[UIComponent("statusLabel")] TextMeshProUGUI statusLabel = null;

		DownloadHistoryEntry entry = null;
        [UIAction("#post-parse")] void Parsed() {BssUi.Text(songName,70,4.5f);BssUi.Text(levelAuthorName,30,4.5f);BssUi.Text(statusLabel,102,3.5f);}

		public CustomDownloadListTableCell PopulateWithSongData(DownloadHistoryEntry entry) {
			this.entry = entry;
			if(songName != null)
				songName.text = entry.songName;
			if(levelAuthorName != null)
				levelAuthorName.text = entry.levelAuthorName;
			if(statusLabel != null)
				statusLabel.text = entry.statusMessage;
			BssTypography.Apply(songName, true);
			BssTypography.Apply(levelAuthorName);
			BssTypography.Apply(statusLabel);

			entry.UpdateProgressHandler = UpdateProgress;
			UpdateProgress();
			return this;
		}

		public override void SelectionDidChange(TransitionType transitionType) => RefreshBgState();
		public override void HighlightDidChange(TransitionType transitionType) => RefreshBgState();
		public override void WasPreparedForReuse() {
			if(entry != null && entry.UpdateProgressHandler == UpdateProgress)
				entry.UpdateProgressHandler = null;
			entry = null;
		}

		[UIComponent("bgContainer")] ImageView bg = null;
		[UIComponent("bgProgress")] ImageView bgProgress = null;
		[UIAction("refresh-visuals")]
		public void RefreshBgState() {
			if(bg != null)
				bg.color = new Color(0, 0, 0, highlighted ? 0.8f : 0.45f);

			RefreshBar();
		}

		void RefreshBar() {
			if(entry == null || bgProgress == null)
				return;

			var clr = entry.status == DownloadStatus.Failed
				? Color.red
				: entry.status == DownloadStatus.Cancelled
					? Color.yellow
					: entry.status == DownloadStatus.Queued
						? Color.gray
						: Color.green;

			clr.a = 0.5f + (Mathf.Clamp01(entry.downloadProgress) * 0.4f);
			bgProgress.color = clr;

			var rect = bgProgress.gameObject.transform as RectTransform;
			if(rect == null)
				return;

			rect.anchorMax = new Vector2(Mathf.Clamp01(entry.downloadProgress), 1);
			rect.ForceUpdateRectTransforms();
		}

		public void UpdateProgress() {
			if(entry == null)
				return;

			if(statusLabel != null)
				statusLabel.text = entry.statusMessage;
			RefreshBar();
		}
	}
}
