using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace BetterSongSearch.Util {
	/// <summary>
	/// Applies BetterSongSearch's local font pair without changing a shared
	/// Beat Saber font asset or its material. Each text receives a material
	/// clone based on its original material, which preserves curved-text shader
	/// properties while pointing the main texture at the local font atlas.
	/// </summary>
	internal static class BssTypography {
		const string RegularFontPath = @"C:\Windows\Fonts\msyh.ttc";
		const string BoldFontPath = @"C:\Windows\Fonts\msyhbd.ttc";
		const int SamplingPointSize = 90;
		const int AtlasPadding = 9;
		const int AtlasWidth = 2048;
		const int AtlasHeight = 2048;
		const string CjkProbe = "中文歌曲";

		static TMP_FontAsset _regularFont;
		static TMP_FontAsset _boldFont;
		static bool _initializationAttempted;
		static readonly HashSet<Material> OwnedMaterials = new HashSet<Material>();
		static readonly Dictionary<int, AppliedText> AppliedTexts = new Dictionary<int, AppliedText>();
		static readonly string[] FontMetricProperties = {
			"_GradientScale",
			"_TextureWidth",
			"_TextureHeight",
			"_ScaleRatioA",
			"_ScaleRatioB",
			"_ScaleRatioC",
			"_ShaderFlags"
		};
		static readonly string[] ClearedStyleProperties = {
			"_FaceDilate",
			"_OutlineWidth",
			"_OutlineSoftness",
			"_GlowOffset",
			"_GlowOuter",
			"_GlowInner",
			"_WeightNormal",
			"_WeightBold"
		};

		sealed class AppliedText {
			public TMP_Text Text;
			public TMP_FontAsset OriginalFont;
			public Material OriginalMaterial;
			public FontStyles OriginalStyle;
			public FontWeight OriginalWeight;
			public TMP_FontAsset AppliedFont;
			public Material AppliedMaterial;
			public FontStyles AppliedStyle;
			public FontWeight AppliedWeight;
		}

		static void LogError(string message, Exception exception = null) {
			var detail = exception == null ? message : message + ": " + exception;
			try {
				if(Plugin.Log != null) {
					Plugin.Log.Error(detail);
					return;
				}
			} catch { }
			Debug.LogError("[BetterSongSearch] " + detail);
		}

		static TMP_FontAsset CreateFont(string path, string name) {
			if(!System.IO.File.Exists(path))
				throw new System.IO.FileNotFoundException("BetterSongSearch font file is missing.", path);

			var font = TMP_FontAsset.CreateFontAsset(
				path,
				0,
				SamplingPointSize,
				AtlasPadding,
				GlyphRenderMode.SDFAA,
				AtlasWidth,
				AtlasHeight
			);
			if(font == null)
				throw new InvalidOperationException("TMP could not create a font asset from " + path);

			font.name = name;
			font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
			string missing;
			if(!font.TryAddCharacters(CjkProbe, out missing, false) || !String.IsNullOrEmpty(missing)) {
				var missingText = String.IsNullOrEmpty(missing) ? "unknown" : missing;
				throw new InvalidOperationException(name + " does not contain the required Chinese glyphs: " + missingText);
			}
			return font;
		}

		static bool EnsureFonts() {
			if(_regularFont != null && _boldFont != null)
				return true;
			if(_initializationAttempted)
				return false;

			_initializationAttempted = true;
			try {
				_regularFont = CreateFont(RegularFontPath, "BetterSongSearch-YaHei-Regular");
				_boldFont = CreateFont(BoldFontPath, "BetterSongSearch-YaHei-Bold");
				return true;
			} catch(Exception exception) {
				if(_regularFont != null)
					DestroyFont(_regularFont);
				_regularFont = null;
				_boldFont = null;
				LogError("Unable to initialize BetterSongSearch typography", exception);
				return false;
			}
		}

		static void CopyFontMetrics(Material generated, Material clone) {
			foreach(var property in FontMetricProperties)
				if(generated.HasProperty(property) && clone.HasProperty(property))
					clone.SetFloat(property, generated.GetFloat(property));

			foreach(var property in ClearedStyleProperties)
				if(clone.HasProperty(property))
					clone.SetFloat(property, 0f);

			if(clone.HasProperty("_OutlineColor"))
				clone.SetColor("_OutlineColor", Color.clear);
			if(clone.HasProperty("_GlowColor"))
				clone.SetColor("_GlowColor", Color.clear);
		}

		static Material CloneMaterial(TMP_FontAsset font, Material source) {
			var template = source ?? font.material;
			var generated = font.material;
			if(template == null || generated == null)
				return null;

			var clone = new Material(template);
			try {
				clone.name = "BetterSongSearch " + font.name + " Material";
				if(template.shader != clone.shader)
					throw new InvalidOperationException("Font material clone changed the source shader.");
				if(font.atlasTexture == null || !clone.HasProperty("_MainTex"))
					throw new InvalidOperationException("Font material has no usable TMP atlas.");
				clone.SetTexture("_MainTex", font.atlasTexture);
				CopyFontMetrics(generated, clone);
				OwnedMaterials.Add(clone);
				return clone;
			} catch {
				UnityEngine.Object.Destroy(clone);
				throw;
			}
		}

		static void ReleaseMaterial(Material material) {
			if(material == null || !OwnedMaterials.Remove(material))
				return;
			UnityEngine.Object.Destroy(material);
		}

		static void EnsureTextCharacters(TMP_FontAsset font, string content) {
			if(font == null || String.IsNullOrEmpty(content))
				return;

			try {
				string missing;
				if(!font.TryAddCharacters(content, out missing, false) && !String.IsNullOrEmpty(missing))
					LogError("Primary BetterSongSearch font could not add text glyphs: " + missing);
			} catch(Exception exception) {
				LogError("Primary BetterSongSearch font glyph population failed", exception);
			}
		}

		static void RefreshMaterial(TMP_FontAsset font, Material material) {
			if(font == null || material == null || font.material == null)
				return;
			if(font.atlasTexture != null && material.HasProperty("_MainTex"))
				material.SetTexture("_MainTex", font.atlasTexture);
			CopyFontMetrics(font.material, material);
		}

		static void PreserveFallbacks(TMP_FontAsset target, TMP_FontAsset original) {
			if(target == null || original == null || target == original || original == _regularFont || original == _boldFont)
				return;

			var fallbacks = target.fallbackFontAssetTable;
			if(fallbacks == null)
				fallbacks = new List<TMP_FontAsset>();
			if(!fallbacks.Contains(original))
				fallbacks.Add(original);
			if(original.fallbackFontAssetTable != null)
				foreach(var fallback in original.fallbackFontAssetTable)
					if(fallback != null && fallback != target && fallback != _regularFont && fallback != _boldFont && !fallbacks.Contains(fallback))
						fallbacks.Add(fallback);
			target.fallbackFontAssetTable = fallbacks;
		}

		/// <summary>
		/// Applies the regular font to every TMP text below <paramref name="root"/>.
		/// The root is intentionally explicit so the global ModUiChinese fallback
		/// remains untouched outside the BetterSongSearch flow.
		/// </summary>
		public static void Apply(GameObject root) {
			if(root == null || !EnsureFonts())
				return;

			foreach(var text in root.GetComponentsInChildren<TMP_Text>(true))
				Apply(text, false);
		}

		/// <summary>
		/// Applies the local font and style to one text component. Font size is
		/// deliberately left unchanged; BSML remains the source of authored sizes.
		/// </summary>
		public static void Apply(TMP_Text text, bool title = false) {
			if(text == null || !EnsureFonts())
				return;

			var targetFont = title ? _boldFont : _regularFont;
			var targetStyle = FontStyles.Normal;
			var targetWeight = FontWeight.Regular;
			EnsureTextCharacters(targetFont, text.text);
			var id = text.GetInstanceID();
			if(!AppliedTexts.TryGetValue(id, out var applied) || applied.Text == null) {
				if(applied != null)
					ReleaseMaterial(applied.AppliedMaterial);
				applied = new AppliedText {
					Text = text,
					OriginalFont = text.font,
					OriginalMaterial = text.fontSharedMaterial,
					OriginalStyle = text.fontStyle,
					OriginalWeight = text.fontWeight
				};
				AppliedTexts[id] = applied;
			}

			if(applied.AppliedFont != targetFont || text.font != targetFont || text.fontSharedMaterial != applied.AppliedMaterial) {
				PreserveFallbacks(targetFont, applied.OriginalFont);
				var sourceMaterial = text.fontSharedMaterial;
				var previousMaterial = applied.AppliedMaterial;
				var material = CloneMaterial(targetFont, sourceMaterial);
				text.font = targetFont;
				if(material != null)
					text.fontSharedMaterial = material;
				else
					text.fontSharedMaterial = targetFont.material;
				ReleaseMaterial(previousMaterial);
				applied.AppliedFont = targetFont;
				applied.AppliedMaterial = material;
			}
			else
				RefreshMaterial(targetFont, applied.AppliedMaterial);

			if(text.fontStyle != targetStyle)
				text.fontStyle = targetStyle;
			if(text.fontWeight != targetWeight)
				text.fontWeight = targetWeight;
			applied.AppliedStyle = targetStyle;
			applied.AppliedWeight = targetWeight;
		}

		/// <summary>
		/// Restores texts still alive and releases generated assets/material clones.
		/// A root may already have been destroyed, in which case Unity's null
		/// semantics simply skip that text.
		/// </summary>
		public static void Dispose() {
			foreach(var applied in AppliedTexts.Values) {
				var text = applied.Text;
				if(text == null)
					continue;
				var restoredFont = text.font == applied.AppliedFont;
				if(restoredFont)
					text.font = applied.OriginalFont;
				if(restoredFont || text.fontSharedMaterial == applied.AppliedMaterial)
					text.fontSharedMaterial = applied.OriginalMaterial;
				if(text.fontStyle == applied.AppliedStyle)
					text.fontStyle = applied.OriginalStyle;
				if(text.fontWeight == applied.AppliedWeight)
					text.fontWeight = applied.OriginalWeight;
			}
			AppliedTexts.Clear();

			foreach(var material in OwnedMaterials)
				if(material != null)
					UnityEngine.Object.Destroy(material);
			OwnedMaterials.Clear();

			DestroyFont(_regularFont);
			DestroyFont(_boldFont);
			_regularFont = null;
			_boldFont = null;
			_initializationAttempted = false;
		}

		static void DestroyFont(TMP_FontAsset font) {
			if(font == null)
				return;
			var atlas = font.atlasTexture;
			var material = font.material;
			try {
				UnityEngine.Object.Destroy(font);
				if(material != null)
					UnityEngine.Object.Destroy(material);
				if(atlas != null)
					UnityEngine.Object.Destroy(atlas);
			} catch(Exception exception) {
				LogError("Unable to release generated BetterSongSearch font", exception);
			}
		}
	}
}
