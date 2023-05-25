using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.WebRTC;
using NativeWebSocket;
using Newtonsoft.Json;
using System;
using System.Text;
using System.Linq;
namespace velshareunity
{
	public class WebRTCReceiver : MonoBehaviour
	{
		//some of these may be unecessary since removing the sender code, which isn't as relevant now that velshare exists
		public string streamRoom = "";
		private int nextId;
		public bool initializeOnStart = true;
		public Material videoMat;
		private Material receivedVideoMat;
		public string iceUrl = "stun:stun.l.google.com:19302";
		public string signalingUrl = "wss://velnet.ugavel.com/ws";
		private RTCPeerConnection localPeerConnection;
		private RTCPeerConnection remotePeerConnection;
		private MediaStream videoStream;
		private WebSocket webSocket;
		public AudioSource outputAudioSource;
		public AudioStreamTrack audioStreamTrack;
		public GameObject previewQuad;
		public List<RTCRtpTransceiver> transceivers = new List<RTCRtpTransceiver>();
		private readonly List<RTCIceCandidate> candidates = new List<RTCIceCandidate>();

		static Coroutine webRTCCoroutine; //we should only have one of these, so it's static, and started by the first one
		public class RpcJSON
		{
			public string jsonrpc = "2.0";
			public string method;
			public string id;
			public object @params;
		}

		private class JoinMessageJSON
		{
			public string sid;
			public OfferJSON offer;
		}

		public class OfferJSON
		{
			public string sdp;
			public string type;
		}

		private class AnswerJSON
		{
			public OfferJSON desc;
		}

		public class CandidateJSON
		{
			public string candidate;
			public string sdpMid;
			public int sdpMLineIndex;
			public string usernameFragment = null;
		}

		public class TrickleJSON
		{
			public int target;
			public CandidateJSON candidate;
		}

		// Start is called before the first frame update
		void Start()
		{
			if (webRTCCoroutine == null)
			{
				webRTCCoroutine = StartCoroutine(WebRTC.Update());
			}
			this.receivedVideoMat = new Material(videoMat);
			previewQuad.GetComponent<MeshRenderer>().material = this.receivedVideoMat;
		}

		// Update is called once per frame
		void Update()
		{
			webSocket?.DispatchMessageQueue();
		}

		public void Shutdown()
		{
			Disconnect();
			localPeerConnection?.Close();
			audioStreamTrack = null;
		}

		private void Connect()
		{
			webSocket?.Connect();
		}

		public void Disconnect()
		{
			if (webSocket?.State == WebSocketState.Open)
			{
				webSocket?.Close();
			}
		}

		public void Startup(string room)
		{
			Shutdown();
			streamRoom = room;
			//connect to the json server
			webSocket = new WebSocket(signalingUrl);
			webSocket.OnOpen += HandleWebsocketOpen;
			webSocket.OnMessage += HandleWebsocketMessage;

			webSocket.OnError += e =>
			{
				Debug.Log("Error! " + e);
			};

			webSocket.OnClose += _ =>
			{
				//Debug.Log("Connection closed!");
			};


			RTCConfiguration configuration = GetSelectedSdpSemantics();
			localPeerConnection = new RTCPeerConnection(ref configuration);
			localPeerConnection.OnIceCandidate = candidate =>
			{
				OnIceCandidate(localPeerConnection, candidate);
			};
			localPeerConnection.OnIceConnectionChange = state =>
			{
				OnIceConnectionChange(localPeerConnection, state);
			};
			localPeerConnection.OnTrack = e =>
			{
				OnTrack(localPeerConnection, e);
			};
			localPeerConnection.OnNegotiationNeeded = () =>
			{
			};

			remotePeerConnection = new RTCPeerConnection();
			remotePeerConnection.OnIceCandidate += (candidate) =>
			{
				//Debug.Log("remote peer got candidate");
				SubmitCandidate(candidate, 1);
			};
			remotePeerConnection.OnDataChannel += _ =>
			{
				//Debug.Log("Data channel received");
			};
			remotePeerConnection.OnTrack += (e) => OnTrack(remotePeerConnection, e);
			remotePeerConnection.OnNegotiationNeeded = () =>
			{
			};


			Connect();

		}

		private RTCConfiguration GetSelectedSdpSemantics()
		{
			RTCConfiguration config = default(RTCConfiguration);
			config.iceServers = new[]
			{
			new RTCIceServer { urls = new[] { iceUrl } }
		};

			return config;
		}

		private void HandleWebsocketMessage(byte[] data)
		{
			string response = Encoding.UTF8.GetString(data);
			//Debug.Log(response);

			Dictionary<string, object> test = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);

			if (test.ContainsKey("method"))
			{
				RpcJSON json = JsonConvert.DeserializeObject<RpcJSON>(response);

				if (json.method == "offer")
				{
					OfferJSON offer = JsonConvert.DeserializeObject<OfferJSON>(JsonConvert.SerializeObject(json.@params)); //this seems stupid
					RtcOffer(offer.sdp);
				}
				else if (json.method == "trickle")
				{
					TrickleJSON trickle = JsonConvert.DeserializeObject<TrickleJSON>(JsonConvert.SerializeObject(json.@params));
					RtcCandidate(trickle);
				}
			}
			else if (test.ContainsKey("result"))
			{
				OfferJSON offerJSON = JsonConvert.DeserializeObject<OfferJSON>(JsonConvert.SerializeObject(test["result"])); //this seems stupid
				RTCSessionDescription desc = new RTCSessionDescription
				{
					sdp = offerJSON.sdp,
					type = RTCSdpType.Answer
				};
				StartCoroutine(OfferAnswered(desc));
			}
		}

		private void SubmitCandidate(RTCIceCandidate candidate, int target)
		{
			if (candidate == null)
			{
				Debug.Log("null candidate");
				return;
			}


			//create a candidate
			CandidateJSON candidateJSON = new CandidateJSON
			{
				candidate = candidate.Candidate,
				sdpMid = candidate.SdpMid,
				sdpMLineIndex = candidate.SdpMLineIndex.Value
			};
			TrickleJSON tj = new TrickleJSON
			{
				target = target,
				candidate = candidateJSON
			};
			RpcJSON json = new RpcJSON
			{
				// id = null;
				method = "trickle",
				@params = tj
			};

			string toSend = JsonConvert.SerializeObject(json);
			webSocket.Send(Encoding.UTF8.GetBytes(toSend));
		}

		private IEnumerator OfferAnswered(RTCSessionDescription desc)
		{
			RTCSetSessionDescriptionAsyncOperation op = localPeerConnection.SetRemoteDescription(ref desc);
			yield return op;
			//here's where we can now process any accumulated ice candidates
			foreach (RTCIceCandidate candidate in candidates)
			{
				SubmitCandidate(candidate, 0);
			}

			candidates.Clear();
		}

		private void HandleWebsocketOpen()
		{
			//Debug.Log("websocket opened");


			StartCoroutine(InitiateRTC());
		}

		private void OnIceCandidate(RTCPeerConnection pc, RTCIceCandidate candidate)
		{
			if (localPeerConnection.IceConnectionState == RTCIceConnectionState.Connected)
			{
				SubmitCandidate(candidate, 0);
			}
			else
			{
				candidates.Add(candidate);
			}
		}


		private void OnIceConnectionChange(RTCPeerConnection pc, RTCIceConnectionState state)
		{
			switch (state)
			{
				case RTCIceConnectionState.New:
					//Debug.Log($"IceConnectionState: New");
					break;
				case RTCIceConnectionState.Checking:
					//Debug.Log($"IceConnectionState: Checking");
					break;
				case RTCIceConnectionState.Closed:
					//Debug.Log($"IceConnectionState: Closed");
					break;
				case RTCIceConnectionState.Completed:
					//Debug.Log($"IceConnectionState: Completed");
					break;
				case RTCIceConnectionState.Connected:
					//Debug.Log($"IceConnectionState: Connected");
					break;
				case RTCIceConnectionState.Disconnected:
					//Debug.Log($"IceConnectionState: Disconnected");
					break;
				case RTCIceConnectionState.Failed:
					//Debug.Log($"IceConnectionState: Failed");
					break;
				case RTCIceConnectionState.Max:
					//Debug.Log($"IceConnectionState: Max");
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(state), state, null);
			}
		}

		private IEnumerator InitiateRTC()
		{
			localPeerConnection.CreateDataChannel("data");

			yield return new WaitForSeconds(1.0f);

			RTCSessionDescriptionAsyncOperation offer = localPeerConnection.CreateOffer();
			yield return offer;
			RTCSessionDescription desc = offer.Desc;
			RTCSetSessionDescriptionAsyncOperation op = localPeerConnection.SetLocalDescription(ref desc);
			yield return op;

			JoinMessageJSON joinMessage = new JoinMessageJSON
			{
				sid = streamRoom,
				offer = new OfferJSON
				{
					type = "offer",
					sdp = desc.sdp
				}
			};

			RpcJSON rpc = new RpcJSON
			{
				id = nextId++ + "",
				method = "join",
				@params = joinMessage
			};

			string toSend = JsonConvert.SerializeObject(rpc);
			//Debug.Log(toSend);

			webSocket.Send(Encoding.UTF8.GetBytes(toSend));
		}

		private void OnTrack(RTCPeerConnection pc, RTCTrackEvent e)
		{
			//Debug.Log("Adding track");
			if (e.Track is AudioStreamTrack audioTrack)
			{
				outputAudioSource.SetTrack(audioTrack);
				outputAudioSource.loop = true;
				outputAudioSource.Play();
			}

			if (e.Track is VideoStreamTrack videoTrack)
			{
				//Debug.Log("Initializing receiving");
				videoTrack.OnVideoReceived += tex =>
				{
					if (this.receivedVideoMat)
					{
						this.receivedVideoMat.mainTexture = tex;
						previewQuad.transform.localScale = new Vector3(tex.width / (float)tex.height, 1, 1);
					}

				};
				//this.rawImage.texture = videoTrack.InitializeReceiver(1920, 1080);

				videoStream = e.Streams.First();
				videoStream.OnRemoveTrack = ev =>
				{
					if (this.receivedVideoMat)
					{
						this.receivedVideoMat.mainTexture = null;
					}

					ev.Track.Dispose();
				};
			}
		}


		//this is received from the remote, and is where I create the remote peer
		private void RtcOffer(string sdp)
		{

			//Debug.Log("got offer: " + sdp);
			RTCSessionDescription offer = new RTCSessionDescription
			{
				sdp = sdp,
				type = RTCSdpType.Offer
			};

			StartCoroutine(SetRemoteDescription(offer));
		}

		private IEnumerator SetRemoteDescription(RTCSessionDescription offer)
		{
			RTCSetSessionDescriptionAsyncOperation op = remotePeerConnection.SetRemoteDescription(ref offer);
			yield return op;
			if (op.IsError)
			{
				Debug.Log(op.Error.message);
			}

			//Debug.Log("Set Remote Description " + offer.sdp);

			RTCSessionDescriptionAsyncOperation op2 = remotePeerConnection.CreateAnswer();
			yield return op2;
			if (op2.IsError)
			{
				Debug.Log(op2.Error.message);
			}

			//Debug.Log("Created answer: " + op2.Desc.sdp);
			RTCSessionDescription desc = op2.Desc;


			RTCSetSessionDescriptionAsyncOperation op3 = remotePeerConnection.SetLocalDescription(ref desc);
			yield return op3;
			if (op3.IsError)
			{
				Debug.Log(op3.Error.message);
			}

			//Debug.Log("Set local description to: " + desc);

			AnswerJSON answerJSON = new AnswerJSON
			{
				desc = new OfferJSON
				{
					type = "answer",
					sdp = desc.sdp
				}
			};
			RpcJSON answer = new RpcJSON
			{
				method = "answer",
				//answer.id = nextId++ + "";
				@params = answerJSON
			};
			string toSend = JsonConvert.SerializeObject(answer);
			webSocket.Send(Encoding.UTF8.GetBytes(toSend));
		}

		//this is from the remote 
		private void RtcCandidate(TrickleJSON trickle)
		{
			//Debug.Log("got rtc candidate: " + trickle.candidate);
			//not so sure about this
			RTCIceCandidateInit init = new RTCIceCandidateInit
			{
				candidate = trickle.candidate.candidate,
				sdpMid = trickle.candidate.sdpMid,
				sdpMLineIndex = trickle.candidate.sdpMLineIndex
			};

			RTCIceCandidate rtcCandidate = new RTCIceCandidate(init);
			if (trickle.target == 0)
			{
				localPeerConnection.AddIceCandidate(rtcCandidate);
			}
			else
			{
				remotePeerConnection.AddIceCandidate(rtcCandidate);
			}
		}


		IEnumerator HandleNegotation(RTCPeerConnection pc)
		{
			yield return null;
		}


		public void OnEnable()
		{
			Startup(streamRoom);
		}
		public void OnDisable()
		{
			Shutdown();
		}

	}

}