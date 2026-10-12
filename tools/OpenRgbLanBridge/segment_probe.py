import socket,json,base64,colorsys,time
ip='192.168.0.153'
s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM)
s.bind(('192.168.0.23',0))
def send(frame):
 x=0
 for b in frame:x^=b
 s.sendto(json.dumps({'msg':{'cmd':'razer','data':{'pt':base64.b64encode(bytes(frame)+bytes([x])).decode()}}}).encode(),(ip,4003))
colors=[tuple(round(v*255) for v in colorsys.hsv_to_rgb(i/10,1,1)) for i in range(10)]
try:
 send([187,0,1,177,1]);time.sleep(.3)
 print('Mire stable: dix segments multicolores pendant 60 secondes.',flush=True)
 end=time.monotonic()+60
 while time.monotonic()<end:
  send([187,0,32,176,1,10]+[v for c in colors for v in c]);time.sleep(.05)
finally:
 send([187,0,1,177,0]);s.close()
 print('Controle externe libere.',flush=True)
