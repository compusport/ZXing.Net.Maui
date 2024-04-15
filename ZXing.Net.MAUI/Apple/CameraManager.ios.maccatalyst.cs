#if IOS || MACCATALYST
using System;
using System.Collections.Generic;
using System.Linq;
using AVFoundation;
using CoreFoundation;
using CoreVideo;
using Foundation;
using UIKit;
using Microsoft.Maui;
using MSize = Microsoft.Maui.Graphics.Size;
using CoreAnimation;
using CoreGraphics;

namespace ZXing.Net.Maui
{
	internal partial class CameraManager
	{
		AVCaptureSession captureSession;
		AVCaptureDevice captureDevice;
		AVCaptureInput captureInput = null;
		PreviewView view;
		CGPoint focusPoint = new(0, 0);
		AVCaptureVideoDataOutput videoDataOutput;
		AVCaptureVideoPreviewLayer videoPreviewLayer;
		CaptureDelegate captureDelegate;
		DispatchQueue dispatchQueue;
		Dictionary<NSString, MSize> Resolutions => new()
		{
			{ AVCaptureSession.Preset352x288, new MSize(352, 288) },
			{ AVCaptureSession.PresetMedium, new MSize(480, 360) },
			{ AVCaptureSession.Preset640x480, new MSize(640, 480) },
			{ AVCaptureSession.Preset1280x720, new MSize(1280, 720) },
			{ AVCaptureSession.Preset1920x1080, new MSize(1920, 1080) },
			{ AVCaptureSession.Preset3840x2160, new MSize(3840, 2160) },
		};

		public NativePlatformCameraPreviewView CreateNativeView()
		{
			captureSession = new AVCaptureSession
			{
				SessionPreset = AVCaptureSession.Preset640x480
			};

			videoPreviewLayer = new AVCaptureVideoPreviewLayer(captureSession);
			videoPreviewLayer.VideoGravity = AVLayerVideoGravity.ResizeAspectFill;

			view = new PreviewView(videoPreviewLayer);

			// ピンチすると拡大縮小
            var pinchGesture = new UIPinchGestureRecognizer((gesture) =>
            {
                if (gesture.State == UIGestureRecognizerState.Changed)
                {
                    var newZoomFactor = captureDevice.VideoZoomFactor * (nfloat)gesture.Scale;
                    newZoomFactor = Math.Max(1.0f, Math.Min((float)newZoomFactor, (float)captureDevice.ActiveFormat.VideoMaxZoomFactor));


                    CaptureDevicePerformWithLockedConfiguration(() =>
                    {
						captureDevice.VideoZoomFactor = (nfloat)newZoomFactor;

                        if (captureDevice.IsFocusModeSupported(AVCaptureFocusMode.AutoFocus))
                        {
                            captureDevice.FocusMode = AVCaptureFocusMode.AutoFocus;
                        }
                    });

                    gesture.Scale = 1;
                }
            });
			view.AddGestureRecognizer(pinchGesture);

			// タップするとフォーカス調整
			var tapGesture = new UITapGestureRecognizer((gesture) =>
			{
                var newZoomFactor = captureDevice.VideoZoomFactor * 1.5f;
                newZoomFactor = Math.Max(1.0f, Math.Min((float)newZoomFactor, (float)captureDevice.ActiveFormat.VideoMaxZoomFactor));
                
				var location = gesture.LocationInView(view);
				var focusPoint = new CGPoint(location.X / view.Bounds.Width, location.Y / view.Bounds.Height);

				//var label = new UILabel();
				//label.Frame = new CGRect(location.X - 50, location.Y - 10, 100, 20);
				//label.TextAlignment = UITextAlignment.Center;
				//label.Text = "🔍";
				//label.BackgroundColor = UIColor.Yellow;
				//label.Layer.CornerRadius = 5;
				//label.ClipsToBounds = true;
				//view.AddSubview(label);

                CaptureDevicePerformWithLockedConfiguration(() =>
                {
                    captureDevice.VideoZoomFactor = (nfloat)newZoomFactor;

                    if (captureDevice.IsFocusModeSupported(AVCaptureFocusMode.AutoFocus))
                    {
						captureDevice.FocusPointOfInterest = focusPoint;
                        captureDevice.FocusMode = AVCaptureFocusMode.AutoFocus;
                    }
					if (captureDevice.IsExposureModeSupported(AVCaptureExposureMode.AutoExpose))
					{
						captureDevice.ExposurePointOfInterest = focusPoint;
						captureDevice.ExposureMode = AVCaptureExposureMode.AutoExpose;
					}
                });

            });
			view.AddGestureRecognizer(tapGesture);


            return view;
		}

		public void Connect()
		{
			UpdateCamera();

			if (videoDataOutput == null)
			{
				videoDataOutput = new AVCaptureVideoDataOutput();

				var videoSettings = NSDictionary.FromObjectAndKey(
					new NSNumber((int)CVPixelFormatType.CV32BGRA),
					CVPixelBuffer.PixelFormatTypeKey);

				videoDataOutput.WeakVideoSettings = videoSettings;

				if (captureDelegate == null)
				{
					captureDelegate = new CaptureDelegate
					{
						SampleProcessor = cvPixelBuffer =>
							FrameReady?.Invoke(this, new CameraFrameBufferEventArgs(new Readers.PixelBufferHolder
								{
									Data = cvPixelBuffer,
									Size = new MSize(cvPixelBuffer.Width, cvPixelBuffer.Height)
								}))
					};
				}

				if (dispatchQueue == null)
					dispatchQueue = new DispatchQueue("CameraBufferQueue");

				videoDataOutput.AlwaysDiscardsLateVideoFrames = true;
				videoDataOutput.SetSampleBufferDelegate(captureDelegate, dispatchQueue);
			}

			captureSession.AddOutput(videoDataOutput);
		}

		public void UpdateCamera()
		{
			if (captureSession != null)
			{
				if (captureSession.Running)
					captureSession.StopRunning();

				// Cleanup old input
				if (captureInput != null && captureSession.Inputs.Length > 0 && captureSession.Inputs.Contains(captureInput))
				{
					captureSession.RemoveInput(captureInput);
					captureInput.Dispose();
					captureInput = null;
				}

				// Cleanup old device
				if (captureDevice != null)
				{
					captureDevice.Dispose();
					captureDevice = null;
				}

				var devices = AVCaptureDevice.DevicesWithMediaType(AVMediaTypes.Video.GetConstant());
				foreach (var device in devices)
				{
					if (CameraLocation == CameraLocation.Front &&
						device.Position == AVCaptureDevicePosition.Front)
					{
						captureDevice = device;
						break;
					}
					else if (CameraLocation == CameraLocation.Rear && device.Position == AVCaptureDevicePosition.Back)
					{
						captureDevice = device;
						break;
					}
				}

				if (captureDevice == null)
					captureDevice = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);

				if (captureDevice is null)
					return;

				captureInput = new AVCaptureDeviceInput(captureDevice, out var err);

				captureSession.AddInput(captureInput);

                captureSession.StartRunning();

            }
        }


		public void Disconnect()
		{
			if (captureSession != null)
			{
				if (captureSession.Running)
					captureSession.StopRunning();

				captureSession.RemoveOutput(videoDataOutput);
				
				// Cleanup old input
				if (captureInput != null && captureSession.Inputs.Length > 0 && captureSession.Inputs.Contains(captureInput))
				{
					captureSession.RemoveInput(captureInput);
					captureInput.Dispose();
					captureInput = null;
				}

				// Cleanup old device
				if (captureDevice != null)
				{
					captureDevice.Dispose();
					captureDevice = null;
				}
			}
		}

		public void UpdateTorch(bool on)
		{
			if (captureDevice != null && captureDevice.HasTorch && captureDevice.TorchAvailable)
			{
				var isOn = captureDevice?.TorchActive ?? false;

				try
				{
					if (on != isOn)
					{
						CaptureDevicePerformWithLockedConfiguration(() =>
							captureDevice.TorchMode = on ? AVCaptureTorchMode.On : AVCaptureTorchMode.Off);
                    }
				}
				catch (Exception ex)
				{
					Console.WriteLine(ex);
				}
			}
		}

		public void Focus(Microsoft.Maui.Graphics.Point point)
		{
			if (captureDevice == null)
				return;

			var focusMode = AVCaptureFocusMode.AutoFocus;
			if (captureDevice.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
				focusMode = AVCaptureFocusMode.ContinuousAutoFocus;

			//See if it supports focusing on a point
			if (captureDevice.FocusPointOfInterestSupported && !captureDevice.AdjustingFocus)
			{
				CaptureDevicePerformWithLockedConfiguration(() =>
				{
					//Focus at the point touched
					captureDevice.FocusPointOfInterest = point;
					captureDevice.FocusMode = focusMode;
				});
			}
		}

		void CaptureDevicePerformWithLockedConfiguration(Action handler)
		{
			if (captureDevice.LockForConfiguration(out var err))
			{
				try
				{
					handler();
				}
				finally
				{
					captureDevice.UnlockForConfiguration();
				}
			}
		}

		public void AutoFocus()
		{
			if (captureDevice == null)
				return;

			var focusMode = AVCaptureFocusMode.AutoFocus;
			if (captureDevice.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
				focusMode = AVCaptureFocusMode.ContinuousAutoFocus;


            CaptureDevicePerformWithLockedConfiguration(() =>
			{
				if (captureDevice.FocusPointOfInterestSupported)
					captureDevice.FocusPointOfInterest = CoreGraphics.CGPoint.Empty;
				captureDevice.FocusMode = focusMode;
                // FIXME: Coud not fix auto focus problem.
                if (captureDevice.AutoFocusRangeRestrictionSupported)
                {
                    captureDevice.AutoFocusRangeRestriction = AVCaptureAutoFocusRangeRestriction.Near;
                }
            });
		}

		public void Dispose()
		{
		}
	}

	class PreviewView : UIView
	{
		public PreviewView(AVCaptureVideoPreviewLayer layer) : base()
		{
			PreviewLayer = layer;

			PreviewLayer.Frame = Layer.Bounds;
			Layer.AddSublayer(PreviewLayer);
		}

		public readonly AVCaptureVideoPreviewLayer PreviewLayer;

		private AVCaptureDevice captureDevice;


        public override void LayoutSubviews()
		{
			base.LayoutSubviews();
			CATransform3D transform = CATransform3D.MakeRotation(0, 0, 0, 1.0f);
			switch (UIDevice.CurrentDevice.Orientation)
			{
				case UIDeviceOrientation.Portrait:
					transform = CATransform3D.MakeRotation(0, 0, 0, 1.0f);
					break;
				case UIDeviceOrientation.PortraitUpsideDown:
					transform = CATransform3D.MakeRotation((nfloat)Math.PI, 0, 0, 1.0f);
					break;
				case UIDeviceOrientation.LandscapeLeft:
					transform = CATransform3D.MakeRotation((nfloat)(-Math.PI / 2), 0, 0, 1.0f);
					break;
				case UIDeviceOrientation.LandscapeRight:
					transform = CATransform3D.MakeRotation((nfloat)Math.PI / 2, 0, 0, 1.0f);
					break;
			}

			PreviewLayer.Transform = transform;
			PreviewLayer.Frame = Layer.Bounds;
		}
	}
}
#endif
