﻿using Composition;
using Composition.Input;
using Composition.Layers;
using Composition.Nodes;
using ImageServer;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace UI.Video
{
    public class CamNode : AnimatedNode
    {
        public FrameNode FrameNode { get; private set; }

        public VideoBounds VideoBounds { get; private set; }

        private OverlayTextNode launchText;

        public event Action<VideoBounds> OnVideoBoundsChange;
        public event Action OnFullScreenRequest;

        public CamNode(FrameSource s, VideoBounds videoBounds)
        {
            VideoBounds = videoBounds;

            //TextNode wtf = new TextNode(s.VideoConfig.DeviceName);
            //Background.AddChild(wtf);

            FrameNode = new FrameNode(s);
            AddChild(FrameNode);
            ReloadConfig();

        }

        private void ReloadConfig()
        {
            if (FrameNode != null)
            {
                FrameNode.Alignment = RectangleAlignment.Center;
                FrameNode.RelativeSourceBounds = VideoBounds.RelativeSourceBounds;
                FrameNode.KeepAspectRatio = !VideoBounds.Crop;
                FrameNode.CropToFit = VideoBounds.Crop;
                FrameNode.RequestLayout();
            }

            if (launchText != null)
            {
                launchText.Dispose();
            }

            if (!string.IsNullOrEmpty(VideoBounds.OverlayText))
            {
                launchText = new OverlayTextNode(VideoBounds.OverlayText, Theme.Current.TextMain.XNA, VideoBounds.OverlayAlignment);
                FrameNode.AddChild(launchText);
                FrameNode.RequestLayout();
            }
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (VideoBounds.SourceType != SourceTypes.FPVFeed)
            {
                if (mouseInputEvent.ButtonState == ButtonStates.Released && mouseInputEvent.Button == MouseButtons.Right && OnVideoBoundsChange != null)
                {
                    MouseMenu mouseMenu = new MouseMenu(this);
                    mouseMenu.TopToBottom = true;

                    mouseMenu.AddItem("Edit Cam Settings", () =>
                    {
                        ShowVideoBoundsEditor();
                    });


                    if (OnFullScreenRequest != null)
                    {
                        mouseMenu.AddItem("Full Screen", () =>
                        {
                            OnFullScreenRequest();
                        });
                    }


                    mouseMenu.Show(mouseInputEvent);
                }
            }

            return base.OnMouseInput(mouseInputEvent);
        }

        public void ShowVideoBoundsEditor()
        {
            VideoBoundsEditor editor = new VideoBoundsEditor(VideoBounds);
            editor.OnOK += Editor_OnOK;
            GetLayer<PopupLayer>().Popup(editor);
        }

        private void Editor_OnOK(BaseObjectEditorNode<VideoBounds> obj)
        {
            OnVideoBoundsChange(obj.Selected);
            ReloadConfig();
        }
    }

    public class OverlayTextNode : TextNode
    {
        public OverlayAlignment OverlayAlignment { get; set; }

        public OverlayTextNode(string text, Color textColor, OverlayAlignment overlayAlignment)
            : base(text, textColor)
        {
            SetAlignment(overlayAlignment);
        }

        public void SetAlignment(OverlayAlignment overlayAlignment)
        {
            OverlayAlignment = overlayAlignment;

            Style.Border = true;

            float height = 0.08f;

            switch (OverlayAlignment)
            {
                case OverlayAlignment.BottomLeft:
                    Alignment = RectangleAlignment.BottomLeft;
                    RelativeBounds = new RectangleF(0, 1 - height, 1, height);
                    break;

                case OverlayAlignment.BottomRight:
                    Alignment = RectangleAlignment.BottomRight;
                    RelativeBounds = new RectangleF(0, 1 - height, 1, height);
                    break;

                case OverlayAlignment.TopLeft:
                    Alignment = RectangleAlignment.TopLeft;
                    RelativeBounds = new RectangleF(0, 0, 1, height);
                    break;

                case OverlayAlignment.TopRight:
                    Alignment = RectangleAlignment.TopRight;
                    RelativeBounds = new RectangleF(0, 0, 1, height);
                    break;
            }

            Scale(0.95f);
        }
    }
}
