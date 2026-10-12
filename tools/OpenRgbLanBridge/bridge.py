"""Single-device OpenRGB SDK v3 -> LightSync Govee LAN protocol proof of concept."""
import base64,json,socket,struct,threading,time,os
from pathlib import Path
IP='192.168.0.153'; LOCAL='192.168.0.23'; PORT=6743; COUNT=10
STOP=Path(__file__).with_name('STOP')
def string(s):
 b=s.encode()+b'\0';return struct.pack('<H',len(b))+b
def device_data(colors):
 body=struct.pack('<I',11)+b''.join(string(s) for s in ['LightSync Bridge - Govee H6061 (10 segments)','Govee','LAN bridge prototype; laptop keyboard unchanged','prototype-1','07:35:D2:38:30:33:04:3F',IP])
 body+=struct.pack('<HI',1,0)+string('Direct')+struct.pack('<12IH',0,32,0,0,0,0,0,0,0,100,0,1,0)
 body+=struct.pack('<H',1)+string('Panels')+struct.pack('<4IH',1,COUNT,COUNT,COUNT,0)
 body+=struct.pack('<H',COUNT)+b''.join(string('Panel '+str(i+1))+struct.pack('<I',i) for i in range(COUNT))
 body+=struct.pack('<H',COUNT)+struct.pack('<10I',*colors)
 return struct.pack('<I',len(body)+4)+body
def govee_frame(colors):
 frame=bytes([187,0,2+COUNT*3,176,1,COUNT])+bytes(v for c in colors for v in [c&255,(c>>8)&255,(c>>16)&255])
 return frame
def lan_packet(frame):
 checksum=0
 for v in frame:checksum^=v
 return json.dumps({'msg':{'cmd':'razer','data':{'pt':base64.b64encode(frame+bytes([checksum])).decode()}}}).encode()
def parse_colors(packet,payload):
 if len(payload)<6 or struct.unpack_from('<I',payload)[0]!=len(payload):raise ValueError('Invalid RGB packet size')
 offset=4
 if packet==1051:
  if len(payload)<10 or struct.unpack_from('<I',payload,4)[0]!=0:raise ValueError('Invalid zone')
  offset=8
 count=struct.unpack_from('<H',payload,offset)[0];offset+=2
 if count!=COUNT or len(payload)!=offset+4*COUNT:raise ValueError('Invalid segment count')
 return list(struct.unpack_from('<10I',payload,offset))
class Bridge:
 def __init__(self):
  self.colors=[0]*COUNT;self.active=False;self.lock=threading.Lock();self.stop=threading.Event();self.updates=0
 def transmit(self):
  sock=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);sock.bind((LOCAL,0));enabled=False;last=None;sent=0
  try:
   while not self.stop.wait(.05):
    with self.lock:active=self.active;colors=self.colors[:]
    if active!=enabled:
     sock.sendto(lan_packet(bytes([187,0,1,177,int(active)])),(IP,4003));enabled=active
    if active and (colors!=last or time.monotonic()-sent>=1):
     sock.sendto(lan_packet(govee_frame(colors)),(IP,4003));last=colors;sent=time.monotonic()
  finally:
   if enabled:sock.sendto(lan_packet(bytes([187,0,1,177,0])),(IP,4003))
   sock.close()
 def reply(self,client,packet,body,dev=0):client.sendall(struct.pack('<4sIII',b'ORGB',dev,packet,len(body))+body)
 def serve(self):
  worker=threading.Thread(target=self.transmit);worker.start()
  print('Bridge ready: 127.0.0.1:6743; Govee '+IP+'; 10 segments; laptop keyboard untouched.',flush=True)
  deadline=time.monotonic()+1800
  try:
   with socket.socket() as server:
    server.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);server.bind(('127.0.0.1',PORT));server.listen(1);server.settimeout(1)
    while time.monotonic()<deadline and not STOP.exists():
     try:client,addr=server.accept()
     except socket.timeout:continue
     print('OpenRGB client connected.',flush=True)
     try:
      with client:
       client.settimeout(1);buffer=b''
       while time.monotonic()<deadline and not STOP.exists():
        try:chunk=client.recv(4096)
        except socket.timeout:continue
        if not chunk:break
        buffer+=chunk
        while len(buffer)>=16:
         magic,dev,packet,size=struct.unpack_from('<4sIII',buffer)
         if magic!=b'ORGB' or size>65536:raise ValueError('Invalid SDK header')
         if len(buffer)<16+size:break
         payload=buffer[16:16+size];buffer=buffer[16+size:]
         if packet==40:self.reply(client,40,struct.pack('<I',3))
         elif packet==0:self.reply(client,0,struct.pack('<I',1))
         elif packet==1 and dev==0:
          with self.lock:colors=self.colors[:]
          self.reply(client,1,device_data(colors))
         elif packet in (1050,1051) and dev==0:
          colors=parse_colors(packet,payload)
          with self.lock:self.colors=colors;self.active=True;self.updates+=1
          if self.updates==1 or self.updates%300==0:print('RGB updates received: '+str(self.updates),flush=True)
     except (OSError,ValueError) as e:print('Client ended: '+str(e),flush=True)
     finally:
      with self.lock:self.active=False
      print('Client disconnected; LAN control released.',flush=True)
  finally:self.stop.set();worker.join()
if __name__=='__main__':Bridge().serve()
