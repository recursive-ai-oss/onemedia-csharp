import sys,struct
# print udta child atoms (type,size) and hex of those named on argv
want=set(sys.argv[2:])
f=open(sys.argv[1],'rb')
def walk(off,end,depth,path):
  while off+8<=end:
    f.seek(off); h=f.read(8); sz,t=struct.unpack('>I4s',h); t=t.decode('latin1')
    hdr=8
    if sz==1: sz=struct.unpack('>Q',f.read(8))[0]; hdr=16
    if sz==0: sz=end-off
    if sz<8: break
    p=path+'/'+t
    if t in ('moov','udta','NCDT','trak','meta') or (t in want and False):
      if t=='meta': walk(off+hdr+4,off+sz,depth+1,p)
      else: walk(off+hdr,off+sz,depth+1,p)
    elif depth>=1 and ('udta' in path or 'NCDT' in path):
      f.seek(off+hdr); d=f.read(min(sz-hdr,int(__import__('os').environ.get('N','96'))))
      print(p,sz-hdr)
      if t in want or 'all' in want: print('  ',d.hex()); print('  ',d.decode('latin1').replace('\x00','.')[:96])
    off+=sz
f.seek(0,2); walk(0,f.tell(),0,'')
