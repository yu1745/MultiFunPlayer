import sys,json,hashlib,os
from pathlib import Path
import numpy as np
from curves import prepare,optimize
rows=json.loads(sys.stdin.read())
# Reuse coefficient helpers, then replace nodes: preprocessing has already occurred in MFP.
s=prepare([{'at':0,'pos':0},{'at':1000,'pos':100}])
t=np.array([x[0] for x in rows],dtype=float)/1e6;y=np.array([x[1] for x in rows],dtype=float)/10000
h=np.diff(t);d=np.diff(y);base=np.zeros(len(t))
for i in range(1,len(t)-1):
 if d[i-1]*d[i]<0:base[i]=np.sign(d[i])*min(4*abs(d[i-1])/h[i-1]**2,4*abs(d[i])/h[i]**2)*.999
base=np.trunc(base*1e6)/1e6
from numpy.polynomial import polynomial as P
from scipy.optimize import minimize
M=np.array([[1,1,1,1],[4,5,6,7],[12,20,30,42],[24,60,120,210]],float)
def coeff(i,a):
 c=np.zeros(8);c[0]=y[i];c[2]=a[i]*h[i]**2/2
 c[4:]=np.linalg.solve(M,[y[i+1]-c[0]-c[2],-2*c[2],a[i+1]*h[i]**2-2*c[2],0]);return c
s.update(t=t,y=y,dt=h,dy=d,acc=base,coeff=coeff)
a,f=optimize(s)
# Stay just inside the validated feasible set before wire quantization.
a=base+(a-base)*.999
sys.stdout.write(json.dumps([int(v*1e6) for v in a]))
